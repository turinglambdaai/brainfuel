import SwiftUI
import AppKit

// MARK: - settings copy helper
// Generated records expose `let` fields; this builds an updated copy.

extension SettingsData {
    func with(size_mode: String? = nil, always_on_top: Bool? = nil,
              language: String? = nil, theme: String? = nil,
              ring_palette: String? = nil, card_opacity_bp: Int64? = nil,
              refresh_interval_minutes: Int64? = nil,
              hourly_remaining: Bool? = nil, weekly_remaining: Bool? = nil,
              notify_enabled: Bool? = nil, notify_threshold: Int64? = nil,
              autostart: Bool? = nil) -> SettingsData {
        SettingsData(
            refresh_interval_minutes: refresh_interval_minutes ?? self.refresh_interval_minutes,
            hourly_remaining: hourly_remaining ?? self.hourly_remaining,
            weekly_remaining: weekly_remaining ?? self.weekly_remaining,
            notify_enabled: notify_enabled ?? self.notify_enabled,
            notify_threshold: notify_threshold ?? self.notify_threshold,
            theme: theme ?? self.theme,
            language: language ?? self.language,
            size_mode: size_mode ?? self.size_mode,
            always_on_top: always_on_top ?? self.always_on_top,
            autostart: autostart ?? self.autostart,
            hotkey_enabled: hotkey_enabled, hotkey_combo: hotkey_combo,
            ring_palette: ring_palette ?? self.ring_palette,
            card_opacity_bp: card_opacity_bp ?? self.card_opacity_bp,
            credential_state: credential_state)
    }
}

// MARK: - palette

struct CardPalette {
    let cardBg: Color
    let border: Color
    let textPrimary: Color
    let textSecondary: Color
    let textMuted: Color
    let track: Color

    @MainActor
    static func resolve(theme: String) -> CardPalette {
        let dark = CardPalette(
            cardBg: Color(red: 0x1F / 255, green: 0x1F / 255, blue: 0x1E / 255),
            border: Color(red: 0xF0 / 255, green: 0xEE / 255, blue: 0xE6 / 255).opacity(0.15),
            textPrimary: Color(red: 0xF0 / 255, green: 0xEE / 255, blue: 0xE6 / 255),
            textSecondary: Color(red: 0xA3 / 255, green: 0x9E / 255, blue: 0x94 / 255),
            textMuted: Color(red: 0x9B / 255, green: 0x96 / 255, blue: 0x8B / 255),
            track: Color(red: 0x3A / 255, green: 0x39 / 255, blue: 0x37 / 255))
        let light = CardPalette(
            cardBg: Color(red: 0xF2 / 255, green: 0xF0 / 255, blue: 0xE9 / 255),
            border: Color(red: 0x80 / 255, green: 0x78 / 255, blue: 0x72 / 255).opacity(0.13),
            textPrimary: Color(red: 0x1F / 255, green: 0x1F / 255, blue: 0x1E / 255),
            textSecondary: Color(red: 0x6E / 255, green: 0x69 / 255, blue: 0x61 / 255),
            textMuted: Color(red: 0x8A / 255, green: 0x86 / 255, blue: 0x7E / 255),
            track: Color(red: 0xDE / 255, green: 0xDB / 255, blue: 0xD2 / 255))
        switch theme {
        case "dark": return dark
        case "light": return light
        default:
            let isDark = NSApp.effectiveAppearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
            return isDark ? dark : light
        }
    }
}

/// Calm ring colors per palette id: (weekly, hourly). Amber/red severity
/// overrides never follow the ring palette (parity with the old widget).
func ringColors(paletteId: String) -> (Color, Color) {
    switch paletteId {
    case "teal": return (Color(red: 0x2A / 255, green: 0xA1 / 255, blue: 0x98 / 255),
                         Color(red: 0x8F / 255, green: 0xD3 / 255, blue: 0xCA / 255))
    case "forest": return (Color(red: 0x5B / 255, green: 0x8C / 255, blue: 0x5A / 255),
                           Color(red: 0xA7 / 255, green: 0xC7 / 255, blue: 0xA1 / 255))
    case "violet": return (Color(red: 0x8B / 255, green: 0x7B / 255, blue: 0xD8 / 255),
                           Color(red: 0xC9 / 255, green: 0xBF / 255, blue: 0xF2 / 255))
    case "mono": return (Color(red: 0x8A / 255, green: 0x86 / 255, blue: 0x80 / 255),
                         Color(red: 0xC9 / 255, green: 0xC4 / 255, blue: 0xBA / 255))
    default: return (Color(red: 0xC2 / 255, green: 0x5E / 255, blue: 0x3E / 255),
                     Color(red: 0xB5 / 255, green: 0x89 / 255, blue: 0x5A / 255))
    }
}

func severityColor(_ severity: Severity, calm: Color) -> Color {
    switch severity {
    case .amber: return Color(red: 0xF5 / 255, green: 0xA6 / 255, blue: 0x23 / 255)
    case .red: return Color(red: 0xE5 / 255, green: 0x48 / 255, blue: 0x4D / 255)
    case .calm: return calm
    }
}

func formatPercent(bp: Int64?) -> String {
    guard let bp else { return "--" }
    let value = Double(bp) / 100.0
    if value.rounded() == value {
        return "\(Int(value))%"
    }
    return String(format: "%.1f%%", value)
}

func relativeTime(ms: Int64, nowMs: Int64) -> String {
    let deltaSec = (ms - nowMs) / 1000
    let future = deltaSec > 0
    let absSec = abs(deltaSec)
    if absSec < 60 {
        return L10n.t("JustNow")
    } else if absSec < 3600 {
        return L10n.t(future ? "MinutesLater" : "MinutesAgo", "\(absSec / 60)")
    } else if absSec < 86400 {
        return L10n.t(future ? "HoursLater" : "HoursAgo", "\(absSec / 3600)")
    } else {
        return L10n.t(future ? "DaysLater" : "DaysAgo", "\(absSec / 86400)")
    }
}

// MARK: - card

struct CardView: View {
    @EnvironmentObject var model: BrainFuelModel

    var body: some View {
        if !model.ready {
            Text(model.status.isEmpty ? "Starting embedded Racket CS…" : model.status)
                .font(.system(size: 12))
                .foregroundColor(.secondary)
                .frame(width: 348, height: 206)
        } else if model.settings.size_mode == "mini" {
            MiniCardView()
                .environmentObject(model)
        } else {
            StandardCardView()
                .environmentObject(model)
        }
    }
}

private struct CardChrome: ViewModifier {
    @EnvironmentObject var model: BrainFuelModel

    func body(content: Content) -> some View {
        let palette = CardPalette.resolve(theme: model.settings.theme)
        content
            .background(
                RoundedRectangle(cornerRadius: 16, style: .continuous)
                    .fill(palette.cardBg.opacity(Double(model.settings.card_opacity_bp) / 10_000.0)))
            .overlay(
                RoundedRectangle(cornerRadius: 16, style: .continuous)
                    .strokeBorder(palette.border, lineWidth: 1))
            .shadow(color: .black.opacity(0.18), radius: 10, y: 3)
    }
}

private struct StandardCardView: View {
    @EnvironmentObject var model: BrainFuelModel
    @State private var showDetails = false

    var body: some View {
        let palette = CardPalette.resolve(theme: model.settings.theme)
        VStack(spacing: 10) {
            HStack(alignment: .top) {
                VStack(alignment: .leading, spacing: 2) {
                    Text("BrainFuel")
                        .font(.system(size: 14, weight: .semibold))
                        .foregroundColor(palette.textPrimary)
                    Text(model.subtitle)
                        .font(.system(size: 11))
                        .foregroundColor(palette.textSecondary)
                        .lineLimit(1)
                }
                Spacer()
                cardMenu(palette: palette)
            }
            HStack(spacing: 14) {
                UsageRingsView(snapshot: model.snapshot, paletteId: model.settings.ring_palette)
                    .frame(width: 96, height: 96)
                VStack(spacing: 8) {
                    UsageChip(title: L10n.t("LblHourly"),
                              usage: model.snapshot?.hourly,
                              severity: model.snapshot?.severity ?? .calm,
                              showRemaining: model.settings.hourly_remaining,
                              hourly: true,
                              palette: palette)
                    UsageChip(title: L10n.t("LblWeekly"),
                              usage: model.snapshot?.weekly,
                              severity: model.snapshot?.severity ?? .calm,
                              showRemaining: model.settings.weekly_remaining,
                              hourly: false,
                              palette: palette)
                }
            }
            footer(palette: palette)
        }
        .padding(14)
        .frame(width: 368, height: 226)
        .modifier(CardChrome())
        .onTapGesture(count: 2) {
            // Double-click toggles mini mode; swallow the first tap's flyout.
            showDetails = false
            model.saveSettings(model.settings.with(size_mode: "mini"))
        }
        .onTapGesture(count: 1) {
            showDetails = true
        }
        .popover(isPresented: $showDetails, arrowEdge: .bottom) {
            DetailsPopover()
                .environmentObject(model)
        }
        .help(failureHelpText())
    }

    private func cardMenu(palette: CardPalette) -> some View {
        Menu {
            Button(L10n.t("MenuDetail")) { showDetails = true }
            Button(L10n.t("MenuRefresh")) { model.refreshNow() }
            if model.accounts.count > 1 {
                Menu(L10n.t("MenuAccounts")) {
                    ForEach(model.accounts, id: \.id) { account in
                        Button(account.name.isEmpty ? account.base_domain : account.name) {
                            model.switchAccount(account.id)
                        }
                    }
                }
            }
            Button(L10n.t("MenuSettings")) { model.onOpenSettings?() }
            Divider()
            Button(model.settings.always_on_top ? L10n.t("MenuTopmostOn") : L10n.t("MenuTopmostOff")) {
                model.saveSettings(model.settings.with(always_on_top: !model.settings.always_on_top))
            }
            Button(L10n.t("MenuMiniMode")) {
                model.saveSettings(model.settings.with(size_mode: "mini"))
            }
            Divider()
            Button(L10n.t("MenuHide")) { NSApp.windows.first?.orderOut(nil) }
            Button(L10n.t("MenuQuit")) { model.onQuit?() }
        } label: {
            Image(systemName: "ellipsis.circle")
                .font(.system(size: 14))
                .foregroundColor(palette.textSecondary)
        }
        .menuStyle(.borderlessButton)
        .fixedSize()
    }

    private func footer(palette: CardPalette) -> some View {
        HStack {
            if let failure = model.snapshot?.failure {
                Text(failure.message)
                    .font(.system(size: 10))
                    .foregroundColor(palette.textMuted)
                    .lineLimit(1)
            } else {
                TimelineView(.periodic(from: .now, by: 30)) { context in
                    let nowMs = Int64(context.date.timeIntervalSince1970 * 1000)
                    Text("\(L10n.t("QuotaDataStatus")) · \(relativeTime(ms: model.snapshot?.fetched_at_ms ?? nowMs, nowMs: nowMs))")
                        .font(.system(size: 10))
                        .foregroundColor(palette.textMuted)
                }
            }
            Spacer()
            Button {
                model.refreshNow()
            } label: {
                if model.refreshing {
                    ProgressView().scaleEffect(0.6)
                } else {
                    Image(systemName: "arrow.clockwise")
                        .font(.system(size: 10))
                }
            }
            .buttonStyle(.plain)
            .foregroundColor(palette.textSecondary)
        }
    }

    private func failureHelpText() -> String {
        guard let failure = model.snapshot?.failure else {
            return L10n.t("TipSizeHint")
        }
        let key = "Failure" + failure.kind.rawValue.prefix(1).uppercased()
            + failure.kind.rawValue.dropFirst()
        return "\(L10n.t(key)): \(failure.message)"
    }
}

private struct UsageRingsView: View {
    let snapshot: QuotaSnapshot?
    let paletteId: String

    var body: some View {
        let (weeklyCalm, hourlyCalm) = ringColors(paletteId: paletteId)
        let weeklyColor = severityColor(snapshot?.severity ?? .calm, calm: weeklyCalm)
        let hourlyColor = severityColor(snapshot?.severity ?? .calm, calm: hourlyCalm)
        ZStack {
            ring(color: weeklyColor, fraction: fraction(snapshot?.weekly.used_bp), lineWidth: 9)
                .padding(3)
            ring(color: hourlyColor, fraction: fraction(snapshot?.hourly.used_bp), lineWidth: 7)
                .padding(17)
        }
        .animation(.easeInOut(duration: 0.5), value: snapshot?.fetched_at_ms)
    }

    private func fraction(_ bp: Int64?) -> CGFloat {
        guard let bp else { return 0 }
        return min(1.0, max(0.0, CGFloat(bp) / 10_000.0))
    }

    private func ring(color: Color, fraction: CGFloat, lineWidth: CGFloat) -> some View {
        ZStack {
            Circle()
                .stroke(Color.gray.opacity(0.25), lineWidth: lineWidth)
            Circle()
                .trim(from: 0, to: fraction)
                .stroke(color, style: StrokeStyle(lineWidth: lineWidth, lineCap: .round))
                .rotationEffect(.degrees(-90))
        }
    }
}

private struct UsageChip: View {
    let title: String
    let usage: WindowUsage?
    let severity: Severity
    let showRemaining: Bool
    let hourly: Bool
    let palette: CardPalette

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack(spacing: 5) {
                Circle()
                    .fill(dotColor)
                    .frame(width: 7, height: 7)
                Text(title)
                    .font(.system(size: 10))
                    .foregroundColor(palette.textSecondary)
            }
            Text(displayPercent)
                .font(.system(size: 20, weight: .semibold, design: .rounded))
                .foregroundColor(dotColor)
            if let reset = usage?.reset_at_ms {
                TimelineView(.periodic(from: .now, by: 30)) { context in
                    let nowMs = Int64(context.date.timeIntervalSince1970 * 1000)
                    Text(L10n.t("DetailResets", relativeTime(ms: reset, nowMs: nowMs)))
                        .font(.system(size: 9))
                        .foregroundColor(palette.textMuted)
                }
            } else {
                Text("--")
                    .font(.system(size: 9))
                    .foregroundColor(palette.textMuted)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var dotColor: Color {
        let classic = ringColors(paletteId: "classic")
        return severityColor(severity, calm: hourly ? classic.1 : classic.0)
    }

    private var displayPercent: String {
        guard let used = usage?.used_bp else { return "--" }
        if showRemaining {
            return formatPercent(bp: max(0, 10_000 - used))
        }
        return formatPercent(bp: used)
    }
}

private struct MiniCardView: View {
    @EnvironmentObject var model: BrainFuelModel

    var body: some View {
        let palette = CardPalette.resolve(theme: model.settings.theme)
        let hourlyUsed = model.snapshot?.hourly.used_bp ?? 0
        let weeklyUsed = model.snapshot?.weekly.used_bp ?? 0
        // The closer-to-exhaustion window drives the single mini ring.
        let showHourly = hourlyUsed >= weeklyUsed
        let used = showHourly ? hourlyUsed : weeklyUsed
        let label = showHourly ? L10n.t("LblHourly") : L10n.t("LblWeekly")
        VStack(spacing: 2) {
            UsageRingsView(snapshot: model.snapshot, paletteId: model.settings.ring_palette)
                .frame(width: 64, height: 64)
            Text(formatPercent(bp: used))
                .font(.system(size: 10, weight: .semibold, design: .rounded))
                .foregroundColor(palette.textPrimary)
            Text(label)
                .font(.system(size: 8))
                .foregroundColor(palette.textMuted)
        }
        .frame(width: 118, height: 118)
        .modifier(CardChrome())
        .onTapGesture(count: 2) {
            model.saveSettings(model.settings.with(size_mode: "standard"))
        }
        .contextMenu {
            Button(L10n.t("MenuSettings")) { model.onOpenSettings?() }
            Button(model.settings.always_on_top ? L10n.t("MenuTopmostOn") : L10n.t("MenuTopmostOff")) {
                model.saveSettings(model.settings.with(always_on_top: !model.settings.always_on_top))
            }
            Button(L10n.t("MenuMiniMode")) {
                model.saveSettings(model.settings.with(size_mode: "standard"))
            }
            Divider()
            Button(L10n.t("MenuQuit")) { model.onQuit?() }
        }
    }
}

// MARK: - details popover

private struct DetailsPopover: View {
    @EnvironmentObject var model: BrainFuelModel
    @State private var details: Details?
    @State private var loadError: String?

    var body: some View {
        let palette = CardPalette.resolve(theme: model.settings.theme)
        VStack(alignment: .leading, spacing: 10) {
            if let snapshot = model.snapshot {
                Text(snapshot.plan_level.isEmpty ? model.subtitle : snapshot.plan_level)
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundColor(palette.textPrimary)
            }
            if let details {
                WindowDetailRow(title: L10n.t("LblHourly"), usage: details.hourly,
                                burn: details.hourly_burn, samples: details.history,
                                hourly: true, palette: palette)
                Divider()
                WindowDetailRow(title: L10n.t("LblWeekly"), usage: details.weekly,
                                burn: details.weekly_burn, samples: details.history,
                                hourly: false, palette: palette)
            } else if let loadError {
                Text(loadError).font(.system(size: 11)).foregroundColor(palette.textMuted)
            } else {
                ProgressView().scaleEffect(0.7)
            }
            Spacer()
            Text(details?.history_note ?? "")
                .font(.system(size: 9))
                .foregroundColor(palette.textMuted)
                .lineLimit(2)
        }
        .padding(12)
        .frame(width: 302, height: 280)
        .task {
            do { details = try await model.details() }
            catch { loadError = String(describing: error) }
        }
    }
}

private struct WindowDetailRow: View {
    let title: String
    let usage: WindowUsage
    let burn: BurnInfo
    let samples: [HistorySample]
    let hourly: Bool
    let palette: CardPalette

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(alignment: .firstTextBaseline) {
                Text(title)
                    .font(.system(size: 10))
                    .foregroundColor(palette.textSecondary)
                Spacer()
                if let reset = usage.reset_at_ms {
                    TimelineView(.periodic(from: .now, by: 30)) { context in
                        let nowMs = Int64(context.date.timeIntervalSince1970 * 1000)
                        Text(L10n.t("DetailResets", relativeTime(ms: reset, nowMs: nowMs)))
                            .font(.system(size: 9))
                            .foregroundColor(palette.textMuted)
                    }
                }
            }
            Text(formatPercent(bp: usage.used_bp))
                .font(.system(size: 24, weight: .bold, design: .rounded))
                .foregroundColor(palette.textPrimary)
            burnLine
            HistoryGraph(samples: samples, hourly: hourly, palette: palette)
                .frame(height: 44)
        }
    }

    private var burnLine: some View {
        Group {
            if let rate = burn.rate_bp_per_hour {
                Text(burnText(rate: rate))
                    .font(.system(size: 9))
                    .foregroundColor(palette.textMuted)
            }
        }
    }

    private func burnText(rate: Int64) -> String {
        let percent = formatPercent(bp: rate).replacingOccurrences(of: "%", with: "")
        let nowMs = Int64(Date().timeIntervalSince1970 * 1000)
        let exhaustion = burn.minutes_to_empty.map {
            relativeTime(ms: nowMs + $0 * 60_000, nowMs: nowMs)
        } ?? "--"
        var text = L10n.t("TipBurn", percent, exhaustion)
        if let comparison = burn.comparison, !comparison.isEmpty {
            text += " \(comparison)"
        }
        return text
    }
}

/// Solid line: samples inside the current period window; dashed: the same
/// window one period earlier (yesterday / last week).
private struct HistoryGraph: View {
    let samples: [HistorySample]
    let hourly: Bool
    let palette: CardPalette

    var body: some View {
        GeometryReader { geo in
            let windowMs: Int64 = hourly ? 5 * 3600_000 : 7 * 86400_000
            let nowMs = Int64(Date().timeIntervalSince1970 * 1000)
            let ordered = samples
                .filter { $0.at_ms > nowMs - windowMs }
                .sorted { $0.at_ms < $1.at_ms }
            let prevOrdered = samples
                .filter { $0.at_ms > nowMs - 2 * windowMs && $0.at_ms <= nowMs - windowMs }
                .sorted { $0.at_ms < $1.at_ms }
            ZStack {
                if ordered.count >= 2 {
                    Path { path in
                        addLine(to: &path, samples: ordered, window: windowMs, anchor: nowMs, size: geo.size)
                    }
                    .stroke(lineColor, style: StrokeStyle(lineWidth: 1.5, lineCap: .round))
                }
                if prevOrdered.count >= 2 {
                    Path { path in
                        addLine(to: &path, samples: prevOrdered, window: windowMs,
                                anchor: nowMs - windowMs, size: geo.size)
                    }
                    .stroke(lineColor.opacity(0.45), style: StrokeStyle(lineWidth: 1, dash: [3, 3]))
                }
                if ordered.count < 2 {
                    Text(L10n.t("HistoryCollecting"))
                        .font(.system(size: 8))
                        .foregroundColor(palette.textMuted)
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                }
            }
        }
    }

    private var lineColor: Color {
        ringColors(paletteId: "classic").0
    }

    private func value(_ s: HistorySample) -> Double? {
        let bp = hourly ? s.hourly_bp : s.weekly_bp
        guard let bp else { return nil }
        return Double(bp) / 10_000.0
    }

    private func addLine(to path: inout Path, samples: [HistorySample], window: Int64,
                         anchor: Int64, size: CGSize) {
        let points: [CGPoint] = samples.compactMap { s in
            guard let v = value(s) else { return nil }
            let x = CGFloat(1.0 - Double(anchor - s.at_ms) / Double(window)) * size.width
            let y = (1.0 - min(1.0, max(0.0, v))) * size.height
            return CGPoint(x: x, y: y)
        }
        guard let first = points.first else { return }
        path.move(to: first)
        for p in points.dropFirst() { path.addLine(to: p) }
    }
}
