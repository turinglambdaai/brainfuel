import SwiftUI
import AppKit
import RivetEmbedding
import RivetRuntime
import RivetSystem

@main
struct BrainFuelApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    // The card is an AppKit panel owned by the delegate; no window scene.
    var body: some Scene {
        Settings { EmptyView() }
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    nonisolated(unsafe) static var shared: AppDelegate?

    private var panel: NSPanel?
    private var settingsWindow: NSWindow?
    private var menuBar: RivetMenuBarController?
    // Retained for the process lifetime: dropping it releases the lease.
    private var instanceLease: RivetSingleInstance?
    private(set) var model: BrainFuelModel?

    func applicationDidFinishLaunching(_ notification: Notification) {
        // Second launches exit; the running instance keeps its card.
        if let lease = try? RivetSingleInstance(applicationID: "site.jrtx.brainfuel") {
            guard lease.isPrimary else {
                NSApp.terminate(nil)
                return
            }
            instanceLease = lease
        }
        Self.shared = self
        NSApp.setActivationPolicy(.accessory)

        let model = BrainFuelModel()
        self.model = model
        installPanel(model: model)
        installMenuBar(model: model)

        model.onOpenSettings = { [weak self] in self?.showSettings() }
        model.onQuit = { NSApp.terminate(nil) }
        model.onSizeModeChanged = { [weak self] in self?.applySizeMode() }
        model.onTopmostChanged = { [weak self] in self?.applyTopmost() }

        model.start()
        applySizeMode()
        positionTopRight()

        Task {
            // Authorization denial is platform policy, and notification APIs
            // need an .app bundle (dev runs skip them entirely).
            if BrainFuelModel.notificationsAvailable {
                _ = try? await RivetNotifications.requestAuthorization()
            }
        }
    }

    func applicationSupportsSecureRestorableState(_ app: NSApplication) -> Bool { true }

    // MARK: card panel

    private func installPanel(model: BrainFuelModel) {
        let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 368, height: 226),
                            styleMask: [.nonactivatingPanel, .titled, .fullSizeContentView],
                            backing: .buffered, defer: false)
        panel.titleVisibility = .hidden
        panel.titlebarAppearsTransparent = true
        panel.isMovableByWindowBackground = true
        panel.becomesKeyOnlyIfNeeded = true
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.standardWindowButton(.closeButton)?.isHidden = true
        panel.standardWindowButton(.miniaturizeButton)?.isHidden = true
        panel.standardWindowButton(.zoomButton)?.isHidden = true
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        // Closing the card hides it (the tray/menu-bar entry brings it back);
        // the only exit path is the explicit Quit item.
        panel.delegate = self

        let hosting = NSHostingView(rootView: CardView().environmentObject(model))
        panel.contentView = hosting
        self.panel = panel
        panel.setFrameTopLeftPoint(topRightPoint(screen: NSScreen.main))
        panel.orderFrontRegardless()
    }

    private func topRightPoint(screen: NSScreen?) -> NSPoint {
        guard let visible = (screen ?? NSScreen.main)?.visibleFrame else {
            return NSPoint(x: 40, y: 40)
        }
        // setFrameTopLeftPoint takes the panel's top-left corner, so the
        // panel width must be subtracted to keep the 20pt right margin.
        let width = panel?.frame.width ?? 368
        return NSPoint(x: visible.maxX - width - 20, y: visible.maxY - 20)
    }

    private func positionTopRight() {
        panel?.setFrameTopLeftPoint(topRightPoint(screen: NSScreen.main))
    }

    func applySizeMode() {
        guard let panel, let model else { return }
        let size: NSSize = model.settings.size_mode == "mini"
            ? NSSize(width: 118, height: 118)
            : NSSize(width: 368, height: 226)
        let topLeft = panel.frame.topLeft
        panel.setContentSize(size)
        panel.setFrameTopLeftPoint(topLeft)
    }

    func applyTopmost() {
        guard let panel, let model else { return }
        panel.level = model.settings.always_on_top ? .floating : .normal
    }

    private func moveToScreen(_ index: Int) {
        let screens = NSScreen.screens
        guard !screens.isEmpty else { return }
        let screen = screens[((index % screens.count) + screens.count) % screens.count]
        panel?.setFrameTopLeftPoint(topRightPoint(screen: screen))
    }

    // MARK: menu bar (tray)

    private func installMenuBar(model: BrainFuelModel) {
        let controller = RivetMenuBarController()
        controller.install(title: "BrainFuel", menuItems: [
            (L10n.t("TrayShow"), "tray-show", { [weak self] in self?.showCard() }),
            (L10n.t("MenuRefresh"), "tray-refresh", { model.refreshNow() }),
            (L10n.t("TraySettings"), "tray-settings", { [weak self] in self?.showSettings() }),
            (L10n.t("MenuQuit"), "tray-quit", { NSApp.terminate(nil) }),
        ])
        menuBar = controller
    }

    func showCard() {
        panel?.orderFrontRegardless()
    }

    // MARK: settings window

    func showSettings() {
        if let window = settingsWindow {
            window.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            return
        }
        guard let model else { return }
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 540, height: 620),
                              styleMask: [.titled, .closable],
                              backing: .buffered, defer: false)
        window.title = L10n.t("MenuSettings")
        window.contentView = NSHostingView(rootView: SettingsView().environmentObject(model))
        window.center()
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        settingsWindow = window
    }
}

extension NSRect {
    var topLeft: NSPoint { NSPoint(x: minX, y: maxY) }
}

// Close = hide to the menu bar; Quit is the only real exit (parity with the
// old tray lifecycle).
extension AppDelegate: NSWindowDelegate {
    func windowShouldClose(_ sender: NSWindow) -> Bool {
        if sender === panel {
            sender.orderOut(nil)
            return false
        }
        return true
    }
}
