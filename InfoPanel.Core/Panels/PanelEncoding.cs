namespace InfoPanel.Panels;

/// <summary>
/// How a panel's frames are sent (<c>devices.json</c>'s <c>encoding</c>): <see cref="Auto"/>, JPEG when the panel is known
/// to take it, else raw; <see cref="Jpeg"/>, always JPEG; <see cref="Raw"/>, never.
/// </summary>
public enum PanelEncoding { Auto, Jpeg, Raw }
