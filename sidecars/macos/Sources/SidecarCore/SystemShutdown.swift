import Foundation

public enum SystemShutdown {
    public static func request() async throws {
        let worker = Task.detached(priority: .userInitiated) {
            let command = Process()
            command.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
            command.arguments = ["-e", "tell application \"System Events\" to shut down"]
            let errors = Pipe()
            let output = Pipe()
            command.standardError = errors
            command.standardOutput = output
            try command.run()
            let deadline = Date().addingTimeInterval(30)
            while command.isRunning {
                if Task.isCancelled || Date() >= deadline {
                    command.terminate()
                    throw NSError(domain: "Optimisarr.Shutdown", code: -1,
                                  userInfo: [NSLocalizedDescriptionKey: "The macOS shutdown request timed out or was canceled."])
                }
                try await Task.sleep(for: .milliseconds(100))
            }
            command.waitUntilExit()
            _ = output.fileHandleForReading.readDataToEndOfFile()
            guard command.terminationStatus == 0 else {
                let data = errors.fileHandleForReading.readDataToEndOfFile()
                let reason = String(data: data, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines)
                throw NSError(domain: "Optimisarr.Shutdown", code: Int(command.terminationStatus),
                              userInfo: [NSLocalizedDescriptionKey: reason?.isEmpty == false ? reason! : "macOS denied the shutdown request."])
            }
        }
        try await withTaskCancellationHandler {
            try await worker.value
        } onCancel: {
            worker.cancel()
        }
    }
}
