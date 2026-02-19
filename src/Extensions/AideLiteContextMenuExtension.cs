// ============================================================================
// AIDE Lite - AI-powered IDE extension for Mendix Studio Pro
// Copyright (c) 2025-2026 Neel Desai / Golden Earth Software Consulting Inc.
// Right-click context menu on microflows — "Explain with AIDE Lite"
// ============================================================================
using System.ComponentModel.Composition;
using Mendix.StudioPro.ExtensionsAPI.Model.Microflows;
using Mendix.StudioPro.ExtensionsAPI.UI.Menu;
using Mendix.StudioPro.ExtensionsAPI.UI.Services;

namespace AideLite.Extensions;

// MEF discovers this class automatically via [Export].
// v10.23 officially supports IEntity and IDocument for ContextMenuExtension<T>.
// IMicroflow (which implements IDocument) works in v10.24+ / v11.
// On v10.23 this is a silent no-op — no crash, just no menu entry.
[Export(typeof(ContextMenuExtension<IMicroflow>))]
public class AideLiteContextMenuExtension : ContextMenuExtension<IMicroflow>
{
    private readonly IDockingWindowService _dockingService;

    [ImportingConstructor]
    public AideLiteContextMenuExtension(IDockingWindowService dockingService)
    {
        _dockingService = dockingService;
    }

    public override IEnumerable<MenuViewModel> GetContextMenus(IMicroflow microflow)
    {
        // Opens the dockable chat pane; user can then ask Claude about the selected microflow
        yield return new MenuViewModel(
            "Explain with AIDE Lite",
            () => _dockingService.OpenPane(AideLitePaneExtension.PaneId)
        );
    }
}
