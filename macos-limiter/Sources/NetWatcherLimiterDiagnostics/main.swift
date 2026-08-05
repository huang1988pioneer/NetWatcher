import Foundation
import NetWatcherLimiterCore

let chromeRule = LimitRule(
    bundleIdentifier: "com.google.Chrome",
    inboundBytesPerSecond: 1024 * 1024,
    outboundBytesPerSecond: 256 * 1024)
let rules = RuleSet(rules: [chromeRule])
let limiter = FlowLimiter()

for index in 1...5 {
    let decision = limiter.allowedBytes(
        appIdentifier: "com.google.Chrome",
        direction: .inbound,
        requestedBytes: 512 * 1024,
        rules: rules)
    print("sample \(index): \(decision)")
}
