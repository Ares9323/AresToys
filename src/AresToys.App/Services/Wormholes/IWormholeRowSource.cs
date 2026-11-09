namespace AresToys.App.Services.Wormholes;

/// <summary>What the Settings view models need from the Wormholes beyond <see cref="IWormholeWindowManager"/>: the list
/// of wormholes as rows, the display order, and the 20 app wide defaults. The main app holds no store and no defaults
/// service any more, so the implementation (<see cref="WormholeWindowManagerProxy"/>) serves copies cached from the
/// satellite. Nothing here hands out a live object of the satellite.</summary>
public interface IWormholeRowSource
{
    /// <summary>Asks the satellite for the wormholes in persisted order and returns copies. When the satellite does not
    /// answer, returns the last copies it had (empty before the first answer).</summary>
    Task<IReadOnlyList<WormholeRecord>> ListAsync(CancellationToken cancellationToken);

    /// <summary>The cached copy of one wormhole, or null when it is unknown. No request.</summary>
    WormholeRecord? Snapshot(Guid wormholeId);

    /// <summary>Moves a row up (-1) or down (+1) in the persisted order. Returns its new index, or -1 when the id is
    /// unknown, the delta is 0 or the satellite did not answer.</summary>
    Task<int> MoveAsync(Guid wormholeId, int delta, CancellationToken cancellationToken);

    /// <summary>The cached app wide defaults. No request; the built in values until the satellite has answered.</summary>
    WormholeWire.DefaultsDto Defaults { get; }

    /// <summary>Asks the satellite for the current defaults, refreshes the cache and returns them. Falls back to the
    /// cache when the satellite does not answer.</summary>
    Task<WormholeWire.DefaultsDto> LoadDefaultsAsync(CancellationToken cancellationToken);

    /// <summary>Writes one default in the satellite, which clamps and saves it. <paramref name="key"/> is one of the
    /// <c>WormholeDefaultsService.*Key</c> constants, <paramref name="value"/> the invariant culture text
    /// (<c>true</c> or <c>false</c> for flags). The cache follows the answer.</summary>
    Task SetDefaultAsync(string key, string value, CancellationToken cancellationToken);
}
