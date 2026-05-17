using Microsoft.Extensions.Logging;

namespace AresToys.Pipeline.Profiles;

public sealed class PipelineProfileSeeder
{
    private readonly IPipelineProfileStore _store;
    private readonly ILogger<PipelineProfileSeeder> _logger;

    public PipelineProfileSeeder(IPipelineProfileStore store, ILogger<PipelineProfileSeeder> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        // Built-ins that have disappeared from DefaultPipelineProfiles.All (e.g. a feature like
        // OCR was tried, persisted, then dropped from the codebase) get DEMOTED to custom rather
        // than deleted: a user might have customised the steps and we don't want to silently
        // throw their work away. After the demotion the orphan shows up under the "Custom" tab,
        // where the user can either keep it or remove it manually with the trash icon.
        // Real customs (IsBuiltIn=false) are never touched — they're already under user control.
        var defaultIds = new HashSet<string>(DefaultPipelineProfiles.All.Select(p => p.Id), StringComparer.Ordinal);
        var existingProfiles = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var existing in existingProfiles)
        {
            if (!existing.IsBuiltIn) continue;
            if (defaultIds.Contains(existing.Id)) continue;
            await _store.UpsertAsync(existing with { IsBuiltIn = false }, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Pipeline profile {Id} demoted to custom (no longer a built-in default).", existing.Id);
        }

        // The profile in DB is the source of truth (user-edited steps must survive restarts), so
        // we only insert defaults when the profile is missing entirely. Older installs may have
        // a profile row predating the Hotkey / IsBuiltIn fields — for those we run a non-
        // destructive upgrade that fills only the missing metadata without touching steps.
        foreach (var profile in DefaultPipelineProfiles.All)
        {
            var existing = await _store.GetAsync(profile.Id, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                await _store.UpsertAsync(profile, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Pipeline profile {Id} seeded with defaults.", profile.Id);
                continue;
            }

            // Don't auto-fill Hotkey: a missing binding can mean "user explicitly cleared it" and
            // we must respect that across restarts. IsBuiltIn is metadata the user can't change,
            // so it's safe to upgrade in-place.
            var upgraded = existing;
            if (!upgraded.IsBuiltIn && profile.IsBuiltIn)
                upgraded = upgraded with { IsBuiltIn = true };

            // One-shot migration for the screen-recording profiles: 0.1.16 → 0.1.17 split the
            // single Toggle-screen-recording task into the 3-step "Record → Save Video file →
            // Add to history" chain (uniform with image-capture pipelines). Stored profiles from
            // before the split are now structurally broken — the lone RecordScreenTask emits
            // bytes into the bag but no downstream step persists them. Detect that exact shape
            // (a single step with task id arestoys.record-screen) and force-upgrade with the
            // current default chain. Users who genuinely customised either profile beyond the
            // single step are left alone — they likely already chained their own Save / AddToHistory.
            if (IsLegacyRecordScreenProfile(profile.Id, existing))
            {
                upgraded = profile;
                _logger.LogInformation("Pipeline profile {Id} migrated 0.1.16 → 0.1.17 record-screen chain.", profile.Id);
            }

            // Same 0.1.16 → 0.1.17 migration for the colour-sampler / colour-picker profiles:
            // collapsed the 8 CopyColorAs* steps into a single ConvertColor + AddText chain.
            // Detect the exact pre-refactor shape (9 steps where the tail is the 8
            // arestoys.copy-color-* task ids in any order) and force-upgrade. Customised
            // profiles with extra / different steps are preserved.
            if (IsLegacyColorProfile(profile.Id, existing))
            {
                upgraded = profile;
                _logger.LogInformation("Pipeline profile {Id} migrated 0.1.16 → 0.1.17 color chain.", profile.Id);
            }

            if (!ReferenceEquals(upgraded, existing))
            {
                await _store.UpsertAsync(upgraded, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Pipeline profile {Id} upgraded with default hotkey / built-in flag.", profile.Id);
            }
            else
            {
                _logger.LogDebug("Pipeline profile {Id} already present; preserving user changes.", profile.Id);
            }
        }
    }

    /// <summary>True when <paramref name="existing"/> matches the pre-0.1.17 single-step shape of
    /// the screen-recording profiles (one RecordScreenTask step that used to do save + history
    /// inline). Those profiles produce no output under the new RecordScreenTask, which only
    /// emits bytes into the bag — so we proactively replace them with the multi-step default.
    /// Anything else (extra steps, different first task) is treated as a real customisation and
    /// preserved.</summary>
    private static bool IsLegacyRecordScreenProfile(string profileId, AresToys.Core.Pipeline.PipelineProfile existing)
    {
        if (profileId != DefaultPipelineProfiles.RecordScreenMp4Id
            && profileId != DefaultPipelineProfiles.RecordScreenGifId)
            return false;
        if (existing.Steps.Count != 1) return false;
        return string.Equals(existing.Steps[0].TaskId, DefaultPipelineProfiles.RecordScreenTaskId, StringComparison.Ordinal);
    }

    /// <summary>True when <paramref name="existing"/> matches the pre-0.1.17 9-step shape of
    /// the colour-sampler / colour-picker profiles (sampler/picker + the 8 CopyColorAs* steps).
    /// Detect by: exactly 9 steps, lead step is the matching colour producer, every step after
    /// is one of the legacy <c>arestoys.copy-color-*</c> task ids.</summary>
    private static bool IsLegacyColorProfile(string profileId, AresToys.Core.Pipeline.PipelineProfile existing)
    {
        string expectedLead;
        if (profileId == DefaultPipelineProfiles.ColorSamplerId) expectedLead = DefaultPipelineProfiles.ColorSamplerTaskId;
        else if (profileId == DefaultPipelineProfiles.ColorPickerId) expectedLead = DefaultPipelineProfiles.ColorPickerTaskId;
        else return false;

        if (existing.Steps.Count != 9) return false;
        if (!string.Equals(existing.Steps[0].TaskId, expectedLead, StringComparison.Ordinal)) return false;
        for (var i = 1; i < existing.Steps.Count; i++)
        {
            var id = existing.Steps[i].TaskId;
            if (id is null || !id.StartsWith("arestoys.copy-color-", StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>Force-overwrite a profile with its default definition. Used by the "Reset to
    /// defaults" button in Settings for users who have made the pipeline unusable.</summary>
    public async Task ResetToDefaultsAsync(string profileId, CancellationToken cancellationToken)
    {
        var profile = DefaultPipelineProfiles.All.FirstOrDefault(p => p.Id == profileId)
            ?? throw new ArgumentException($"unknown profile id '{profileId}'", nameof(profileId));
        await _store.UpsertAsync(profile, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Pipeline profile {Id} reset to defaults.", profileId);
    }
}
