#if os(macOS)
import SwiftUI
import UbitProtocol
import Virtualization

struct ContentView: View {
    @EnvironmentObject private var model: AppModel
    @State private var showingScore = false

    var body: some View {
        VStack(spacing: 0) {
            toolbar
            Divider()
            content
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    // MARK: Toolbar

    private var toolbar: some View {
        HStack(spacing: 12) {
            Text("ubit").font(.headline)
            statusText.foregroundStyle(.secondary).lineLimit(1)
            Spacer()
            scoreButton
            switch model.phase {
            case .stopped, .failed:
                if model.bundle.hasDisk {
                    Button("Start") { model.start() }.keyboardShortcut("r", modifiers: .command)
                }
            case .running:
                Button("Shut down") { model.stop() }
            case .stopping:
                Button("Force stop") { model.forceStop() }
            default:
                EmptyView()
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
    }

    private var statusText: Text {
        switch model.phase {
        case .needsImage: return Text("No image yet")
        case .importing: return Text("Importing image…")
        case .stopped: return Text("Stopped")
        case .starting: return Text("Starting…")
        case .stopping: return Text("Shutting down…")
        case .failed: return Text("Something went wrong")
        case .running:
            switch model.agent {
            case .connecting: return Text("Running · waiting for the agent…")
            case .scoring: return Text("Running · scoring your apps…")
            case .unavailable: return Text("Running · agent not reachable")
            default: return Text("Running" + (model.guestInfo.map { " · \($0)" } ?? ""))
            }
        }
    }

    @ViewBuilder
    private var scoreButton: some View {
        if let summary = model.score {
            Button {
                showingScore.toggle()
            } label: {
                Label(summary.score.map { "Readiness \($0)/100" } ?? "Readiness: no score", systemImage: "checkmark.seal")
            }
            .popover(isPresented: $showingScore, arrowEdge: .bottom) {
                ScoreView(summary: summary).frame(width: 420, height: 380)
            }
        } else if case .unavailable(let why) = model.agent {
            Button("Retry agent") { model.talkToAgent() }.help(why)
        }
    }

    // MARK: Main area

    @ViewBuilder
    private var content: some View {
        switch model.phase {
        case .needsImage:
            Onboarding(choose: model.chooseImage)
        case .importing:
            ProgressView("Copying the image…")
        case .starting, .running, .stopping:
            if let vm = model.vm {
                VMView(virtualMachine: vm)
            } else {
                ProgressView()
            }
        case .stopped:
            VStack(spacing: 12) {
                Text("ubit is stopped").font(.title2)
                Button("Start ubit") { model.start() }.controlSize(.large).buttonStyle(.borderedProminent)
                Button("Replace image…") { model.chooseImage() }.buttonStyle(.link)
            }
        case .failed(let message):
            VStack(spacing: 12) {
                Text(message).multilineTextAlignment(.center).frame(maxWidth: 520)
                Button("Choose a different image…") { model.chooseImage() }
            }
        }
    }
}

private struct Onboarding: View {
    let choose: () -> Void

    var body: some View {
        VStack(spacing: 16) {
            Text("Try ubit on this Mac").font(.largeTitle.bold())
            Text("ubit runs in a window, next to your Mac apps. Nothing on your Mac is changed, and your app list never leaves this computer.")
                .multilineTextAlignment(.center)
                .foregroundStyle(.secondary)
                .frame(maxWidth: 520)
            Button("Choose image…", action: choose)
                .controlSize(.large)
                .buttonStyle(.borderedProminent)
            Text("Developers: run scripts/fetch-image.sh to download and verify the arm64 image.")
                .font(.footnote)
                .foregroundStyle(.tertiary)
        }
        .padding(40)
    }
}

private struct ScoreView: View {
    let summary: ScoreSummary

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .firstTextBaseline) {
                Text(summary.score.map(String.init) ?? "–").font(.system(size: 40, weight: .bold))
                Text("of 100").foregroundStyle(.secondary)
            }
            Text("The full report is open inside ubit.").font(.callout).foregroundStyle(.secondary)
            List(summary.items) { item in
                HStack {
                    VStack(alignment: .leading) {
                        Text(item.from)
                        Text("→ \(item.to)").font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer()
                    Text(item.status.rawValue.capitalized)
                        .font(.caption.bold())
                        .padding(.horizontal, 8).padding(.vertical, 2)
                        .background(color(item.status).opacity(0.15), in: Capsule())
                        .foregroundStyle(color(item.status))
                }
            }
            .listStyle(.plain)
        }
        .padding(16)
    }

    private func color(_ status: ItemStatus) -> Color {
        switch status {
        case .native: return .green
        case .alternative: return .blue
        case .workaround: return .orange
        case .blocker: return .red
        }
    }
}

/// Hosts the VM's display. Resizing the window resizes the guest display (macOS 14+).
struct VMView: NSViewRepresentable {
    let virtualMachine: VZVirtualMachine

    func makeNSView(context: Context) -> VZVirtualMachineView {
        let view = VZVirtualMachineView()
        view.virtualMachine = virtualMachine
        view.capturesSystemKeys = true
        view.automaticallyReconfiguresDisplay = true
        return view
    }

    func updateNSView(_ view: VZVirtualMachineView, context: Context) {
        if view.virtualMachine !== virtualMachine {
            view.virtualMachine = virtualMachine
        }
    }
}
#endif
