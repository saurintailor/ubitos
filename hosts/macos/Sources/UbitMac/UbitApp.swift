#if os(macOS)
import AppKit
import SwiftUI

@main
struct UbitApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @StateObject private var model = AppModel()

    var body: some Scene {
        WindowGroup("ubit") {
            ContentView()
                .environmentObject(model)
                .frame(minWidth: 1024, minHeight: 680)
        }
        .commands {
            CommandGroup(replacing: .newItem) {}
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        // Needed when launched as a bare executable (swift run) rather than an .app bundle.
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        true
    }
}
#else
// The app is macOS-only; this lets `swift build` and the protocol tests run on Linux CI.
@main
enum UbitApp {
    static func main() {
        print("UbitMac runs on macOS only.")
    }
}
#endif
