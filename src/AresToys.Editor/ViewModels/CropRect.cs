namespace AresToys.Editor.ViewModels;

/// <summary>Pending non-destructive crop rectangle on <see cref="EditorViewModel.PendingCrop"/>.
/// Lives separately from the regular shape list because crop is an action ("apply this rect
/// to the bitmap") rather than an annotation ("draw this rect onto the bitmap") — keeping
/// it off the Shapes collection avoids polluting the undo stack until the user confirms,
/// and avoids tangling crop preview rendering with the normal shape adorner / selection
/// pipeline. Confirmation runs <see cref="EditorViewModel.ApplyCrop"/> (the destructive
/// command); Cancel just clears the property.</summary>
public sealed record CropRect(double X, double Y, double Width, double Height);
