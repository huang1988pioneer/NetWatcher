import Foundation

public final class TokenBucket: @unchecked Sendable {
    private let lock = NSLock()
    private let rateBytesPerSecond: Double
    private let capacityBytes: Double
    private var tokens: Double
    private var lastRefill: ContinuousClock.Instant

    public init(bytesPerSecond: Int, burstSeconds: Double = 1.0) {
        let rate = max(1, bytesPerSecond)
        self.rateBytesPerSecond = Double(rate)
        self.capacityBytes = max(Double(rate), Double(rate) * max(0.1, burstSeconds))
        self.tokens = self.capacityBytes
        self.lastRefill = ContinuousClock.now
    }

    public func decision(for byteCount: Int, now: ContinuousClock.Instant = .now) -> TokenDecision {
        lock.lock()
        defer { lock.unlock() }

        refill(now: now)
        let requested = max(0, Double(byteCount))
        if requested <= tokens {
            tokens -= requested
            return .allow
        }

        let deficit = requested - tokens
        let delay = deficit / rateBytesPerSecond
        return .delay(seconds: max(0.01, delay))
    }

    public func consumeAvailable(upTo byteCount: Int, now: ContinuousClock.Instant = .now) -> Int {
        lock.lock()
        defer { lock.unlock() }

        refill(now: now)
        let allowed = min(Double(max(0, byteCount)), tokens)
        tokens -= allowed
        return Int(allowed.rounded(.down))
    }

    private func refill(now: ContinuousClock.Instant) {
        let elapsed = lastRefill.duration(to: now).components
        let seconds = Double(elapsed.seconds) + Double(elapsed.attoseconds) / 1_000_000_000_000_000_000
        if seconds > 0 {
            tokens = min(capacityBytes, tokens + seconds * rateBytesPerSecond)
            lastRefill = now
        }
    }
}

public enum TokenDecision: Equatable, Sendable {
    case allow
    case delay(seconds: Double)
}
