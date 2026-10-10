namespace LabelScope.Core.Graphics;

/// <summary>
/// Graphic data that cannot be read (bad Base64, damaged compression, an unreadable image). The message is plain
/// language and complete, so the renderer can show it as a warning as it is.
/// </summary>
internal sealed class GraphicDataException(string message) : Exception(message);
