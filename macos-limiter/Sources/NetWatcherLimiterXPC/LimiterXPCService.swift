import Foundation
import NetWatcherLimiterCore
import NetWatcherLimiterHostSupport

public final class LimiterXPCService: NSObject, LimiterXPCProtocol {
    private let configurationManager: FilterConfigurationManager
    private let ruleWriter: RuleWriter
    private let decoder = JSONDecoder()
    private let encoder = JSONEncoder()

    public init(
        configurationManager: FilterConfigurationManager = FilterConfigurationManager(),
        ruleWriter: RuleWriter = RuleWriter()
    ) {
        self.configurationManager = configurationManager
        self.ruleWriter = ruleWriter
        self.encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
    }

    public func setFilterEnabled(_ enabled: Bool, withReply reply: @escaping (Bool, String?) -> Void) {
        configurationManager.setEnabled(enabled) { result in
            switch result {
            case .success:
                reply(true, nil)
            case .failure(let error):
                reply(false, error.localizedDescription)
            }
        }
    }

    public func replaceRules(_ jsonData: Data, withReply reply: @escaping (Bool, String?) -> Void) {
        do {
            let rules = try decoder.decode(RuleSet.self, from: jsonData)
            try ruleWriter.replaceRules(rules)
            reply(true, nil)
        } catch {
            reply(false, error.localizedDescription)
        }
    }

    public func upsertRule(_ jsonData: Data, withReply reply: @escaping (Bool, String?) -> Void) {
        do {
            let rule = try decoder.decode(LimitRule.self, from: jsonData)
            try ruleWriter.upsertRule(rule)
            reply(true, nil)
        } catch {
            reply(false, error.localizedDescription)
        }
    }

    public func removeRule(_ bundleIdentifier: String, withReply reply: @escaping (Bool, String?) -> Void) {
        do {
            try ruleWriter.removeRule(bundleIdentifier: bundleIdentifier)
            reply(true, nil)
        } catch {
            reply(false, error.localizedDescription)
        }
    }

    public func status(withReply reply: @escaping (Data?, String?) -> Void) {
        configurationManager.load { [encoder] result in
            switch result {
            case .success(let snapshot):
                do {
                    reply(try encoder.encode(snapshot), nil)
                } catch {
                    reply(nil, error.localizedDescription)
                }
            case .failure(let error):
                reply(nil, error.localizedDescription)
            }
        }
    }
}
