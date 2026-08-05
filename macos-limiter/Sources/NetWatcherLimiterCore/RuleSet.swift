import Foundation

public struct RuleSet: Codable, Equatable, Sendable {
    public var rules: [LimitRule]

    public init(rules: [LimitRule] = []) {
        self.rules = rules
    }

    public func rule(for bundleIdentifier: String?) -> LimitRule? {
        guard let bundleIdentifier, !bundleIdentifier.isEmpty else {
            return nil
        }

        return rules.first {
            $0.bundleIdentifier.caseInsensitiveCompare(bundleIdentifier) == .orderedSame
        }
    }
}
