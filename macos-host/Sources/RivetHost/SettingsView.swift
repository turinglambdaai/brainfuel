import SwiftUI
import AppKit

/// Settings window. Edits apply on change and are saved through the backend
/// RPC (the backend clamps and persists atomically).
struct SettingsView: View {
    @EnvironmentObject var model: BrainFuelModel
    @State private var tab = 0

    var body: some View {
        VStack(spacing: 0) {
            Picker("", selection: $tab) {
                Text(L10n.t("TabGeneral")).tag(0)
                Text(L10n.t("TabAccount")).tag(1)
                Text(L10n.t("TabNotifications")).tag(2)
                Text(L10n.t("TabSoftware")).tag(3)
            }
            .pickerStyle(.segmented)
            .padding(12)
            Divider()
            Group {
                switch tab {
                case 0: GeneralTab()
                case 1: AccountTab()
                case 2: NotificationsTab()
                default: SoftwareTab()
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .frame(width: 540, height: 620)
    }
}

private extension View {
    func row() -> some View { padding(.vertical, 6) }
}

// MARK: - general

private struct GeneralTab: View {
    @EnvironmentObject var model: BrainFuelModel

    var body: some View {
        Form {
            appearanceSection
            behaviorSection
            quotaSection
            startupSection
        }
        .formStyle(.grouped)
        .onChange(of: model.settings.theme) { theme in
            NSApp.appearance = theme == "dark" ? NSAppearance(named: .darkAqua)
                : theme == "light" ? NSAppearance(named: .aqua) : nil
        }
    }

    private var appearanceSection: some View {
        Section(L10n.t("SectionAppearance")) {
            Picker(L10n.t("LblLanguage"), selection: languageBinding) {
                Text("中文").tag("zh")
                Text("English").tag("en")
            }
            Picker(L10n.t("LblTheme"), selection: themeBinding) {
                Text(L10n.t("ThemeSystem")).tag("system")
                Text(L10n.t("ThemeLight")).tag("light")
                Text(L10n.t("ThemeDark")).tag("dark")
            }
            Picker(L10n.t("LblRingPalette"), selection: paletteBinding) {
                Text("Classic").tag("classic")
                Text("Teal").tag("teal")
                Text("Forest").tag("forest")
                Text("Violet").tag("violet")
                Text("Mono").tag("mono")
            }
            VStack(alignment: .leading) {
                Text("\(L10n.t("LblOpacity")): \(model.settings.card_opacity_bp / 100)%")
                Slider(value: Binding(
                    get: { Double(model.settings.card_opacity_bp) / 100.0 },
                    set: { model.saveSettings(model.settings.with(card_opacity_bp: Int64($0 * 100))) }),
                    in: 30...100, step: 5)
            }.row()
        }
    }

    private var behaviorSection: some View {
        Section(L10n.t("SectionDesktopBehavior")) {
            Toggle(L10n.t("ChkAlwaysOnTop"), isOn: topmostBinding).row()
            Picker(L10n.t("LblInterval"), selection: intervalBinding) {
                ForEach([1, 5, 10, 15, 30, 60], id: \.self) { minutes in
                    Text("\(minutes) \(L10n.t("UnitMinutes"))").tag(Int64(minutes))
                }
            }
        }
    }

    private var quotaSection: some View {
        Section(L10n.t("SectionQuotaDisplay")) {
            Toggle(L10n.t("ChkHourlyRemaining"), isOn: hourlyRemainingBinding).row()
            Toggle(L10n.t("ChkWeeklyRemaining"), isOn: weeklyRemainingBinding).row()
        }
    }

    private var startupSection: some View {
        Section(L10n.t("SectionStartup")) {
            Toggle(L10n.t("ChkAutostart"), isOn: autostartBinding).row()
        }
    }

    private var languageBinding: Binding<String> {
        Binding(get: { model.settings.language },
                set: { model.saveSettings(model.settings.with(language: $0)) })
    }

    private var themeBinding: Binding<String> {
        Binding(get: { model.settings.theme },
                set: { model.saveSettings(model.settings.with(theme: $0)) })
    }

    private var paletteBinding: Binding<String> {
        Binding(get: { model.settings.ring_palette },
                set: { model.saveSettings(model.settings.with(ring_palette: $0)) })
    }

    private var topmostBinding: Binding<Bool> {
        Binding(get: { model.settings.always_on_top },
                set: { model.saveSettings(model.settings.with(always_on_top: $0)) })
    }

    private var hourlyRemainingBinding: Binding<Bool> {
        Binding(get: { model.settings.hourly_remaining },
                set: { model.saveSettings(model.settings.with(hourly_remaining: $0)) })
    }

    private var weeklyRemainingBinding: Binding<Bool> {
        Binding(get: { model.settings.weekly_remaining },
                set: { model.saveSettings(model.settings.with(weekly_remaining: $0)) })
    }

    private var autostartBinding: Binding<Bool> {
        Binding(get: { model.settings.autostart },
                set: { model.saveSettings(model.settings.with(autostart: $0)) })
    }

    private var intervalBinding: Binding<Int64> {
        Binding(get: { model.settings.refresh_interval_minutes },
                set: { model.saveSettings(model.settings.with(refresh_interval_minutes: $0)) })
    }
}

// MARK: - account

private struct AccountTab: View {
    @EnvironmentObject var model: BrainFuelModel
    @State private var name = ""
    @State private var platform = "cn"
    @State private var apiKey = ""
    @State private var message = ""
    @State private var busy = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Picker(L10n.t("ManageAccounts"), selection: Binding(
                get: { model.activeAccountId },
                set: { model.switchAccount($0) })) {
                ForEach(model.accounts, id: \.id) { account in
                    Text(display(account)).tag(account.id)
                }
            }

            HStack {
                Button(L10n.t("RemoveAccount")) {
                    guard !model.activeAccountId.isEmpty else { return }
                    busy = true
                    Task {
                        defer { busy = false }
                        try? await model.removeAccount(model.activeAccountId)
                    }
                }
                .disabled(model.accounts.isEmpty || busy)
            }
            .controlSize(.small)

            Divider()

            Form {
                Section(L10n.t("SectionAccount")) {
                    TextField(L10n.t("LblAccountName"), text: $name,
                              prompt: Text(L10n.t("AccountNamePlaceholder")))
                    Picker(L10n.t("LblPlatform"), selection: $platform) {
                        Text(L10n.t("PlatformCn")).tag("cn")
                        Text(L10n.t("PlatformIntl")).tag("intl")
                        Text(L10n.t("PlatformCodex")).tag("codex")
                        Text(L10n.t("PlatformClaude")).tag("claude")
                    }
                    .onChange(of: platform) { _ in apiKey = ""; message = "" }
                    if platform == "codex" || platform == "claude" {
                        Text(L10n.t("CliLoginHint"))
                            .font(.footnote).foregroundColor(.secondary)
                    } else {
                        SecureField(L10n.t("LblApiKey"), text: $apiKey,
                                    prompt: Text(L10n.t("KeyPlaceholder")))
                        HStack {
                            Link(L10n.t("KeyOpenConsole"), destination:
                                    platform == "intl"
                                    ? URL(string: "https://z.ai/manage-apikey/apikey-list")!
                                    : URL(string: "https://open.bigmodel.cn/usercenter/proj-mgmt/apikeys")!)
                                .font(.footnote)
                            Spacer()
                            Button(L10n.t("BtnSave")) { saveAccount() }
                                .buttonStyle(.borderedProminent)
                                .disabled(busy
                                          || (platform != "codex" && platform != "claude" && apiKey.isEmpty))
                        }
                    }
                    Text(message.isEmpty ? L10n.t("AccountDesc") : message)
                        .font(.footnote).foregroundColor(.secondary)
                }
            }
            .formStyle(.grouped)

            Spacer()
        }
        .padding(14)
    }

    private func display(_ account: RivetTypes.Account) -> String {
        let label = account.name.isEmpty ? account.base_domain : account.name
        return account.configured ? label : "\(label) · \(L10n.t("NotConfigured"))"
    }

    private func saveAccount() {
        busy = true
        let draft: RivetTypes.AccountDraft
        switch platform {
        case "codex":
            draft = RivetTypes.AccountDraft(id: nil, name: name, provider: .codex,
                                 base_domain: "https://chatgpt.com",
                                 api_key: nil, clear_key: false)
        case "claude":
            draft = RivetTypes.AccountDraft(id: nil, name: name, provider: .claude,
                                 base_domain: "https://api.anthropic.com",
                                 api_key: nil, clear_key: false)
        case "intl":
            draft = RivetTypes.AccountDraft(id: nil, name: name, provider: .glm,
                                 base_domain: "https://api.z.ai",
                                 api_key: apiKey, clear_key: false)
        default:
            draft = RivetTypes.AccountDraft(id: nil, name: name, provider: .glm,
                                 base_domain: "https://open.bigmodel.cn",
                                 api_key: apiKey, clear_key: false)
        }
        Task {
            defer { busy = false }
            do {
                try await model.saveAccount(draft)
                name = ""; apiKey = ""; message = ""
            } catch {
                message = String(describing: error)
            }
        }
    }
}

// MARK: - notifications

private struct NotificationsTab: View {
    @EnvironmentObject var model: BrainFuelModel

    var body: some View {
        Form {
            Section(L10n.t("SectionNotifications")) {
                Toggle(L10n.t("ChkNotify"), isOn: Binding(
                    get: { model.settings.notify_enabled },
                    set: { model.saveSettings(model.settings.with(notify_enabled: $0)) })).row()
                VStack(alignment: .leading) {
                    Text("\(L10n.t("LblThreshold")): \(model.settings.notify_threshold)%")
                    Slider(value: Binding(
                        get: { Double(model.settings.notify_threshold) },
                        set: { model.saveSettings(model.settings.with(notify_threshold: Int64($0))) }),
                        in: 10...99, step: 1)
                    Text(L10n.t("NotificationsDesc"))
                        .font(.footnote).foregroundColor(.secondary)
                }.row()
            }
        }
        .formStyle(.grouped)
    }
}

// MARK: - software

private struct SoftwareTab: View {
    @EnvironmentObject var model: BrainFuelModel
    @State private var copied = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("\(L10n.t("UpdateCurrentVersion")): BrainFuel \(RivetGeneratedConfig.version)")
                .font(.title3.weight(.semibold))
            Text(L10n.t("SoftwareDesc"))
                .font(.callout).foregroundColor(.secondary)
            HStack {
                Button(L10n.t("BtnCheckUpdates")) {
                    model.checkForUpdates()
                }
                .disabled(model.updatePhase == .downloading)
                if model.updatePhase == .checking {
                    ProgressView().scaleEffect(0.7)
                    Text(L10n.t("UpdateCheckingShort"))
                        .font(.footnote).foregroundColor(.secondary)
                } else if model.updatePhase == .available {
                    Text(L10n.t("UpdateAvailableTitle"))
                        .font(.footnote).foregroundColor(.secondary)
                } else if model.updatePhase == .upToDate {
                    Text(L10n.t("UpdateUpToDateShort"))
                        .font(.footnote).foregroundColor(.secondary)
                } else if model.updatePhase == .failed {
                    Text(L10n.t("UpdateFailedShort"))
                        .font(.footnote).foregroundColor(.secondary)
                }
            }
            Divider()
            Text(L10n.t("SoftwareTrustTitle")).font(.headline)
            Text(L10n.t("SoftwareTrustDesc"))
                .font(.footnote).foregroundColor(.secondary)
            Spacer()
            HStack {
                Button(copied ? L10n.t("DiagnosticsCopied") : L10n.t("DiagnosticsCopy")) { copyDiagnostics() }
                Link(L10n.t("UpdateOpenDownloads"), destination:
                        URL(string: "https://github.com/turinglambdaai/brainfuel/releases")!)
            }
            Text(model.status).font(.footnote).foregroundColor(.secondary)
        }
        .padding(16)
    }

    private func copyDiagnostics() {
        Task {
            let text = (try? await model.diagnostics()) ?? ""
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(text, forType: .string)
            copied = true
            try? await Task.sleep(nanoseconds: 2_000_000_000)
            copied = false
        }
    }
}
