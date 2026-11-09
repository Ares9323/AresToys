namespace AresToys.App.ViewModels;

/// <summary>One icon entry in the picker grid. <see cref="Glyph"/> is the actual char (single
/// codepoint from FontAwesome's Private Use Area) that gets stored in <c>Category.Icon</c> and
/// rendered by any TextBlock with <c>FontFamily="{StaticResource IconFont}"</c>.
/// <see cref="Name"/> is the FontAwesome slug — shown as a tooltip so the user can look up the
/// icon in the FA cheatsheet without guessing.</summary>
public sealed record IconCatalogEntry(string Glyph, string Name);

/// <summary>Curated FontAwesome 7 Free Solid icons available in the picker. Bigger than the
/// initial sample because the original 60 felt thin — this version covers ~200 glyphs across
/// every common "what is this category about" axis (folders, code, devops, web, docs, media,
/// communication, shapes, charts, system, security, locations, transport, calendar, status,
/// hardware, science, weather, food, sport, animals).
///
/// Glyphs are FA PUA codepoints written as <c>"\uXXXX"</c> escape sequences so the source
/// stays plain ASCII — pasting raw PUA chars into the file made the editor swallow them on
/// save. Codepoints come from the FontAwesome 7 cheatsheet (https://fontawesome.com/icons);
/// new entries can be appended freely — the picker grid auto-fits.</summary>
public static class IconCatalog
{
    public static readonly IReadOnlyList<IconCatalogEntry> All =
    [
        // Files & folders
        new("", "file"), new("", "file-lines"), new("", "file-code"),
        new("", "file-image"), new("", "file-video"), new("", "file-audio"),
        new("", "file-pdf"), new("", "file-word"), new("", "file-excel"),
        new("", "file-powerpoint"), new("", "file-zipper"), new("", "file-import"),
        new("", "file-export"), new("", "file-arrow-up"), new("", "file-arrow-down"),
        new("", "file-circle-plus"), new("", "file-circle-minus"), new("", "file-shield"),
        new("", "folder"), new("", "folder-open"), new("", "folder-closed"),
        new("", "folder-tree"), new("", "folder-plus"), new("", "folder-minus"),
        new("", "copy"), new("", "paste"), new("", "scissors"),
        new("", "clipboard"), new("", "clipboard-list"), new("", "clipboard-check"),

        // Code / dev
        new("", "code"), new("", "code-branch"), new("", "code-compare"),
        new("", "code-fork"), new("", "code-merge"), new("", "code-pull-request"),
        new("", "terminal"), new("", "cube"), new("", "cubes"),
        new("", "network-wired"), new("", "database"), new("", "gear"),
        new("", "gears"), new("", "server"), new("", "bug"),
        new("", "shield-halved"), new("", "robot"), new("", "microchip"),
        new("", "diagram-project"), new("", "sitemap"),

        // Web / links / share
        new("", "link"), new("", "link-slash"), new("", "external-link"),
        new("", "globe"), new("", "earth-europe"), new("", "earth-americas"),
        new("", "earth-asia"), new("", "eye"), new("", "eye-slash"),
        new("", "magnifying-glass"), new("", "magnifying-glass-plus"),
        new("", "rss"), new("", "share"), new("", "share-nodes"),
        new("", "wifi"), new("", "tower-broadcast"), new("", "satellite-dish"),
        new("", "cloud"), new("", "cloud-arrow-up"), new("", "cloud-arrow-down"),

        // Docs / writing
        new("", "note-sticky"), new("", "book"), new("", "book-open"),
        new("", "bookmark"), new("", "pen"), new("", "pen-clip"),
        new("", "pencil"), new("", "square-pen"), new("", "tag"),
        new("", "tags"), new("", "highlighter"), new("", "marker"),
        new("", "list"), new("", "list-ol"), new("", "list-ul"),
        new("", "list-check"), new("", "table"), new("", "table-list"),
        new("", "language"), new("", "spell-check"), new("", "text-width"),

        // Media
        new("", "image"), new("", "images"), new("", "film"),
        new("", "video"), new("", "video-slash"), new("", "music"),
        new("", "microphone"), new("", "microphone-slash"),
        new("", "headphones"), new("", "volume-high"), new("", "volume-low"),
        new("", "volume-xmark"), new("", "play"), new("", "pause"),
        new("", "stop"), new("", "forward"), new("", "backward"),
        new("", "podcast"), new("", "radio"), new("", "tv"),

        // Communication / social
        new("", "envelope"), new("", "envelope-open"), new("", "envelopes-bulk"),
        new("", "comment"), new("", "comments"), new("", "comment-dots"),
        new("", "phone"), new("", "phone-volume"), new("", "fax"),
        new("", "bell"), new("", "bullhorn"), new("", "users"),
        new("", "user"), new("", "user-group"), new("", "user-plus"),
        new("", "address-book"), new("", "address-card"), new("", "id-card"),

        // Status / favorites / flags
        new("", "star"), new("", "star-half"), new("", "star-of-life"),
        new("", "heart"), new("", "heart-circle-bolt"),
        new("", "circle-exclamation"), new("", "exclamation"),
        new("", "circle-question"), new("", "circle-info"),
        new("", "circle-check"), new("", "check"), new("", "xmark"),
        new("", "ban"), new("", "flag"), new("", "flag-checkered"),
        new("", "thumbs-up"), new("", "thumbs-down"), new("", "puzzle-piece"),

        // System / security
        new("", "lock"), new("", "lock-open"), new("", "unlock"),
        new("", "key"), new("", "user-shield"), new("", "user-secret"),
        new("", "user-circle"), new("", "user-tie"), new("", "user-pen"),
        new("", "calendar"), new("", "calendar-day"), new("", "calendar-week"),
        new("", "calendar-check"), new("", "clock"), new("", "stopwatch"),
        new("", "hourglass"), new("", "wrench"), new("", "screwdriver"),
        new("", "screwdriver-wrench"), new("", "toolbox"), new("", "hammer"),
        new("", "trash"), new("", "trash-can"), new("", "broom"),
        new("", "rotate-left"), new("", "rotate-right"), new("", "arrows-rotate"), new("", "undo"),

        // Charts / data / money
        new("", "chart-area"), new("", "chart-pie"), new("", "chart-bar"),
        new("", "chart-line"), new("", "chart-column"), new("", "chart-simple"),
        new("", "percent"), new("", "calculator"), new("", "money-bill"),
        new("", "credit-card"), new("", "wallet"), new("", "coins"),
        new("", "sack-dollar"), new("", "scale-balanced"), new("", "scale-unbalanced"),

        // Hardware / devices
        new("", "computer"), new("", "desktop"), new("", "laptop"),
        new("", "tablet"), new("", "mobile"), new("", "mobile-screen"),
        new("", "keyboard"), new("", "computer-mouse"), new("", "print"),
        new("", "memory"), new("", "hard-drive"), new("", "plug"),
        new("", "battery-full"), new("", "battery-half"), new("", "battery-empty"),

        // Locations / transport
        new("", "house"), new("", "building"), new("", "city"),
        new("", "warehouse"), new("", "shop"), new("", "store"),
        new("", "location-dot"), new("", "map"), new("", "map-pin"),
        new("", "compass"), new("", "route"), new("", "road"),
        new("", "car"), new("", "truck"), new("", "bus"),
        new("", "train"), new("", "plane"), new("", "ship"),
        new("", "bicycle"), new("", "motorcycle"), new("", "rocket"),

        // Weather / nature
        new("", "sun"), new("", "moon"), new("", "cloud-sun"),
        new("", "cloud-moon"), new("", "cloud-rain"), new("", "cloud-bolt"),
        new("", "snowflake"), new("", "wind"), new("", "umbrella"),
        new("", "fire"), new("", "tree"), new("", "leaf"),
        new("", "seedling"), new("", "mountain"), new("", "water"),

        // Activities / things
        new("", "gamepad"), new("", "dice"), new("", "trophy"),
        new("", "medal"), new("", "award"), new("", "crown"),
        new("", "gift"), new("", "cake-candles"), new("", "champagne-glasses"),
        new("", "mug-hot"), new("", "utensils"), new("", "pizza-slice"),
        new("", "burger"), new("", "ice-cream"),
        new("", "dumbbell"), new("", "person-running"), new("", "futbol"),
        new("", "basketball"), new("", "baseball"), new("", "volleyball"),

        // Health / science
        new("", "heart-pulse"), new("", "stethoscope"), new("", "syringe"),
        new("", "pills"), new("", "capsules"), new("", "flask"),
        new("", "atom"), new("", "magnet"), new("", "lightbulb"),
        new("", "bolt"),

        // Animals
        new("", "cat"), new("", "dog"), new("", "fish"),
        new("", "horse"), new("", "frog"), new("", "dragon"),
        new("", "spider"), new("", "paw"),
    ];
}
