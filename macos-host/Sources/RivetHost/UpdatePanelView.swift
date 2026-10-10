import SwiftUI

/// Update panel (shared/spec/UPDATE.md), ported from taskly's UpdateSheet:
/// one phase machine driving check → offer → download → install. A failed
/// install leaves the running version untouched; the download phase cannot
/// be dismissed (no cancel support). The host presents it in a small
/// dedicated window — the card is a borderless panel and the settings
/// window may not be open when a silent check finds an offer.
struct UpdatePanelView: View {
    @EnvironmentObject var model: BrainFuelModel

    var body: some View {
        VStack(spacing: 16) {
            Image(systemName: "arrow.down.circle")
                .font(.system(size: 44))
                .foregroundStyle(Color.accentColor)

            switch model.updatePhase {
            case .checking:
                Text(L10n.t("UpdateChecking"))
                    .font(.system(size: 13))
                    .foregroundStyle(.secondary)
                ProgressView()

            case .available:
                Text(L10n.t("UpdateAvailableTitle"))
                    .font(.system(size: 15, weight: .semibold))
                Text(L10n.t("UpdateAvailableBody", model.updateAvailableVersion))
                    .font(.system(size: 13))
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                HStack(spacing: 12) {
                    Button(L10n.t("BtnCancel")) { model.dismissUpdatePanel() }
                    Button(L10n.t("UpdateRestart")) { model.installUpdate() }
                        .keyboardShortcut(.defaultAction)
                        .buttonStyle(.borderedProminent)
                }

            case .downloading:
                Text(L10n.t("UpdateDownloading"))
                    .font(.system(size: 13))
                ProgressView(value: Double(model.updateProgressPercent), total: 100)
                    .frame(width: 240)
                Text("\(model.updateProgressPercent)%")
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)

            case .upToDate:
                Text(L10n.t("UpdateUpToDate"))
                    .font(.system(size: 14, weight: .medium))
                Button(L10n.t("BtnOK")) { model.dismissUpdatePanel() }
                    .keyboardShortcut(.defaultAction)
                    .buttonStyle(.borderedProminent)

            case .failed:
                Text(model.updateErrorMessage)
                    .font(.system(size: 13))
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: 320)
                    .fixedSize(horizontal: false, vertical: true)
                HStack(spacing: 12) {
                    Button(L10n.t("BtnCancel")) { model.dismissUpdatePanel() }
                    Button(L10n.t("BtnCheckUpdates")) { model.checkForUpdates() }
                }

            case .idle:
                EmptyView()
            }
        }
        .padding(28)
        .frame(width: 360)
        .background(.regularMaterial)
    }
}
