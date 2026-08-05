import Foundation

guard let line = readLine(), let data = line.data(using: .utf8) else {
    print("{\"success\":false,\"message\":\"Expected one JSON request on standard input.\"}")
    exit(64)
}

do {
    let request = try JSONDecoder().decode(LimiterControlRequest.self, from: data)
    var response = LimiterControlResponse(success: false, message: "No response from host.")
    var completed = false
    LimiterControlHandler().handle(request) {
        response = $0
        completed = true
    }
    let deadline = Date().addingTimeInterval(20)
    while !completed && Date() < deadline {
        RunLoop.current.run(mode: .default, before: Date().addingTimeInterval(0.05))
    }
    guard completed else {
        print("{\"success\":false,\"message\":\"Limiter Host timed out.\"}")
        exit(70)
    }
    let encoder = JSONEncoder()
    encoder.outputFormatting = [.sortedKeys]
    print(String(decoding: try encoder.encode(response), as: UTF8.self))
} catch {
    let message = error.localizedDescription.replacingOccurrences(of: "\"", with: "\\\"")
    print("{\"success\":false,\"message\":\"\(message)\"}")
    exit(65)
}
