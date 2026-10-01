import SwiftUI
import RivetEmbedding
import RivetRuntime
import RivetSystem

/// Main-actor model bridging the embedded Racket backend and the card UI.
/// All business logic lives in the backend; this only renders and forwards.
@MainActor
final class BrainFuelModel: ObservableObject {
    @MainActor
    private final class EventRelay {
        weak var model: BrainFuelModel?

        init(_ model: BrainFuelModel) { self.model = model }

        func receive(_ event: RivetEvent) {
            model?.receive(event)
        }

        func ready(accounts: [Account], activeId: String,
                   snapshot: QuotaSnapshot?, settings: SettingsData) {
            model?.bootstrap(accounts: accounts, activeId: activeId,
                             snapshot: snapshot, settings: settings)
        }

        func fail(_ message: String) {
            guard let model else { return }
            model.ready = false
            model.status = message
        }
    }

    @Published var ready = false
    @Published var status = ""
    @Published var accounts: [Account] = []
    @Published var activeAccountId = ""
    @Published var snapshot: QuotaSnapshot?
    @Published var settings = SettingsData(
        refresh_interval_minutes: 5, hourly_remaining: true, weekly_remaining: false,
        notify_enabled: true, notify_threshold: 80, theme: "dark", language: "zh",
        size_mode: "standard", always_on_top: false, autostart: false,
        hotkey_enabled: false, hotkey_combo: "", ring_palette: "classic",
        card_opacity_bp: 10_000, credential_state: "none")
    @Published var refreshing = false

    var onOpenSettings: (() -> Void)?
    var onQuit: (() -> Void)?
    var onSizeModeChanged: (() -> Void)?
    var onTopmostChanged: (() -> Void)?

    private var backend: EmbeddedRacketBackend?

    var activeAccount: Account? {
        accounts.first { $0.id == activeAccountId }
    }

    var subtitle: String {
        if accounts.count > 1 {
            return activeAccount?.name ?? activeAccount?.base_domain ?? ""
        }
        return L10n.t("CardSubtitle")
    }

    func start() {
        guard backend == nil else { return }
        do {
            let config = try EmbeddedRacketConfiguration.resolvedDefault(
                moduleName: RivetGeneratedConfig.moduleName,
                entryName: RivetGeneratedConfig.entryName)
            let backend = EmbeddedRacketBackend(configuration: config)
            let relay = EventRelay(self)
            self.backend = backend
            status = "Starting embedded Racket CS…"
            Task.detached { [backend, relay] in
                do {
                    try backend.start { name, value in
                        guard let event = try? RivetEvent.decode(name: name, value: value) else { return }
                        Task { @MainActor in relay.receive(event) }
                    }
                    let api = RivetAPI(client: backend.client)
                    // initialize starts the backend refresh scheduler.
                    try await api.initialize()
                    async let accounts = api.getAccounts()
                    async let active = api.getActive_account_id()
                    async let snapshot = api.getSnapshot()
                    async let settings = api.get_settings()
                    let (a, id, s, cfg) = try await (accounts, active, snapshot, settings)
                    await relay.ready(accounts: a, activeId: id, snapshot: s, settings: cfg)
                } catch {
                    await relay.fail(String(describing: error))
                }
            }
        } catch {
            status = "Configuration error: \(error)"
        }
    }

    private func bootstrap(accounts: [Account], activeId: String,
                           snapshot: QuotaSnapshot?, settings: SettingsData) {
        self.accounts = accounts
        self.activeAccountId = activeId
        self.snapshot = snapshot
        self.settings = settings
        L10n.language = settings.language
        ready = true
        status = ""
    }

    // MARK: events

    /// UNUserNotificationCenter.current() throws an uncatchable NSException
    /// when the process is not running from an .app bundle (every
    /// `raco rivet dev` run). Guard on the bundle shape first.
    static var notificationsAvailable: Bool {
        Bundle.main.bundleURL.pathExtension == "app"
    }

    private func receive(_ event: RivetEvent) {
        switch event {
        case .quota_updated(let snap):
            snapshot = snap
        case .alert_triggered(let alert):
            // The backend owns thresholds and hysteresis; the host delivers.
            guard Self.notificationsAvailable else { return }
            Task {
                try? await RivetNotifications.show(
                    title: "BrainFuel",
                    body: alert.message,
                    identifier: "alert-\(alert.account_id)-\(alert.which)")
            }
        }
    }

    // MARK: actions

    func refreshNow() {
        guard let backend, ready, !refreshing else { return }
        refreshing = true
        Task {
            // The backend single-flights manual + timer refreshes.
            _ = try? await RivetAPI(client: backend.client).refresh_now()
            refreshing = false
        }
    }

    func switchAccount(_ id: String) {
        guard let backend, ready else { return }
        Task {
            let api = RivetAPI(client: backend.client)
            try? await api.switch_account(id: id)
            activeAccountId = id
            snapshot = try? await api.getSnapshot()
        }
    }

    func saveAccount(_ draft: AccountDraft) async throws {
        guard let backend else { return }
        let api = RivetAPI(client: backend.client)
        accounts = try await api.save_account(draft: draft)
        if activeAccountId.isEmpty, let first = accounts.first {
            switchAccount(first.id)
        }
    }

    func removeAccount(_ id: String) async throws {
        guard let backend else { return }
        let api = RivetAPI(client: backend.client)
        accounts = try await api.remove_account(id: id)
        if !accounts.contains(where: { $0.id == activeAccountId }) {
            if let first = accounts.first {
                activeAccountId = first.id
                snapshot = try? await api.getSnapshot()
            } else {
                activeAccountId = ""
                snapshot = nil
            }
        }
    }

    func saveSettings(_ cfg: SettingsData) {
        guard let backend, ready else { return }
        let previous = settings
        settings = cfg
        L10n.language = cfg.language
        if cfg.size_mode != previous.size_mode { onSizeModeChanged?() }
        if cfg.always_on_top != previous.always_on_top { onTopmostChanged?() }
        Task {
            do {
                let saved = try await RivetAPI(client: backend.client).save_settings(settings: cfg)
                settings = saved
                L10n.language = saved.language
                if saved.autostart != previous.autostart {
                    try RivetLoginItem.setEnabled(saved.autostart)
                }
            } catch {
                settings = previous
                L10n.language = previous.language
                status = String(describing: error)
            }
        }
    }

    func details() async throws -> Details? {
        guard let backend, ready else { return nil }
        return try await RivetAPI(client: backend.client)
            .get_details(account_id: activeAccountId)
    }

    func diagnostics() async throws -> String {
        guard let backend, ready else { return "" }
        return try await RivetAPI(client: backend.client).get_diagnostics()
    }
}
