import Foundation
#if SWIFT_PACKAGE
import NetWatcherLimiterCore
#endif

public final class RuleWriter: @unchecked Sendable {
    private let store: RuleStore

    public init(rulesURL: URL = LimiterPaths.rulesURL()) {
        self.store = RuleStore(url: rulesURL)
    }

    public func replaceRules(_ rules: RuleSet) throws {
        try store.save(rules)
    }

    public func upsertRule(_ rule: LimitRule) throws {
        var ruleSet = store.load()
        if let index = ruleSet.rules.firstIndex(where: {
            $0.bundleIdentifier.caseInsensitiveCompare(rule.bundleIdentifier) == .orderedSame
        }) {
            ruleSet.rules[index] = rule
        } else {
            ruleSet.rules.append(rule)
        }

        try store.save(ruleSet)
    }

    public func removeRule(bundleIdentifier: String) throws {
        var ruleSet = store.load()
        ruleSet.rules.removeAll {
            $0.bundleIdentifier.caseInsensitiveCompare(bundleIdentifier) == .orderedSame
        }

        try store.save(ruleSet)
    }
}
