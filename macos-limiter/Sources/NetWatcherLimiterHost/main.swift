import Foundation
import NetWatcherLimiterHostSupport

enum HostCommandLine {
    static func run() {
        guard let line = readLine(), let data = line.data(using: .utf8) else {
            write(LimiterControlResponse(success: false, message: "Expected one JSON request on standard input."))
            return
        }

        do {
            let request = try JSONDecoder().decode(LimiterControlRequest.self, from: data)
            var response = LimiterControlResponse(success: false, message: "No response from host.")
            var completed = false
            LimiterControlHandler().handle(request) { result in
                response = result
                completed = true
            }
            let deadline = Date().addingTimeInterval(20)
            while !completed && Date() < deadline {
                RunLoop.current.run(mode: .default, before: Date().addingTimeInterval(0.05))
            }
            if !completed {
                response = LimiterControlResponse(success: false, message: "Limiter Host timed out.")
            }
            write(response)
        } catch {
            write(LimiterControlResponse(success: false, message: error.localizedDescription))
        }
    }

    private static func write(_ response: LimiterControlResponse) {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        guard let data = try? encoder.encode(response),
              let text = String(data: data, encoding: .utf8) else {
            return
        }
        print(text)
    }
}

HostCommandLine.run()
