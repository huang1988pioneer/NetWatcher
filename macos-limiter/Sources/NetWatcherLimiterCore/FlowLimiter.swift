import Foundation

public final class FlowLimiter: @unchecked Sendable {
    private let lock = NSLock()
    private var buckets: [BucketKey: BucketEntry] = [:]

    public init() {}

    public func allowedBytes(
        appIdentifier: String?,
        direction: LimitDirection,
        requestedBytes: Int,
        rules: RuleSet
    ) -> LimitDecision {
        guard
            let rule = rules.rule(for: appIdentifier),
            let limit = rule.limit(for: direction),
            limit > 0
        else {
            return .unlimited
        }

        let key = BucketKey(appIdentifier: rule.bundleIdentifier, direction: direction)
        let bucket = bucket(for: key, bytesPerSecond: limit)
        let allowed = bucket.consumeAvailable(upTo: requestedBytes)
        if allowed > 0 {
            return .pass(bytes: allowed)
        }

        return .pause(seconds: 0.05)
    }

    private func bucket(for key: BucketKey, bytesPerSecond: Int) -> TokenBucket {
        lock.lock()
        defer { lock.unlock() }

        if let entry = buckets[key], entry.bytesPerSecond == bytesPerSecond {
            return entry.bucket
        }

        let bucket = TokenBucket(bytesPerSecond: bytesPerSecond)
        buckets[key] = BucketEntry(bytesPerSecond: bytesPerSecond, bucket: bucket)
        return bucket
    }
}

public enum LimitDecision: Equatable, Sendable {
    case unlimited
    case pass(bytes: Int)
    case pause(seconds: Double)
}

private struct BucketKey: Hashable {
    var appIdentifier: String
    var direction: LimitDirection
}

private struct BucketEntry {
    var bytesPerSecond: Int
    var bucket: TokenBucket
}
