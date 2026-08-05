import Foundation
import AppKit
#if SWIFT_PACKAGE
import NetWatcherLimiterCore
#endif

public final class LimiterControlHandler: @unchecked Sendable {
    private let configurationManager: FilterConfigurationManager
    private let ruleWriter: RuleWriter

    public init(configurationManager: FilterConfigurationManager = FilterConfigurationManager(), ruleWriter: RuleWriter? = nil) {
        self.configurationManager = configurationManager
        self.ruleWriter = ruleWriter ?? RuleWriter(rulesURL: configurationManager.rulesURL)
    }

    public func handle(_ request: LimiterControlRequest, completion: @escaping (LimiterControlResponse) -> Void) {
        switch request.command {
        case .status:
            configurationManager.load { result in completion(Self.configurationResponse(result)) }
        case .setEnabled:
            guard let enabled = request.enabled else {
                completion(.init(success: false, message: "Missing enabled value."))
                return
            }
            configurationManager.setEnabled(enabled) { result in completion(Self.configurationResponse(result)) }
        case .replaceRules:
            guard let rules = request.rules else {
                completion(.init(success: false, message: "Missing rules."))
                return
            }
            do {
                try ruleWriter.replaceRules(rules)
                completion(.init(success: true, message: "Rules saved."))
            } catch {
                completion(.init(success: false, message: error.localizedDescription))
            }
        case .upsertRule:
            guard let rule = request.rule else {
                completion(.init(success: false, message: "Missing rule."))
                return
            }
            do {
                try ruleWriter.upsertRule(rule)
                completion(.init(success: true, message: "Rule saved."))
            } catch {
                completion(.init(success: false, message: error.localizedDescription))
            }
        case .upsertProcessRule:
            guard let processIdentifier = request.processIdentifier,
                  let bundleIdentifier = NSRunningApplication(processIdentifier: pid_t(processIdentifier))?.bundleIdentifier else {
                completion(.init(success: false, message: "The process does not have a resolvable macOS bundle identifier."))
                return
            }
            let rule = LimitRule(
                bundleIdentifier: bundleIdentifier,
                inboundBytesPerSecond: request.inboundBytesPerSecond,
                outboundBytesPerSecond: request.outboundBytesPerSecond,
                isEnabled: true,
                blockConnections: request.blockConnections ?? false)
            do {
                try ruleWriter.upsertRule(rule)
                completion(.init(success: true, message: "Rule saved.", bundleIdentifier: bundleIdentifier))
            } catch {
                completion(.init(success: false, message: error.localizedDescription))
            }
        case .removeRule:
            guard let bundleIdentifier = request.bundleIdentifier, !bundleIdentifier.isEmpty else {
                completion(.init(success: false, message: "Missing bundle identifier."))
                return
            }
            do {
                try ruleWriter.removeRule(bundleIdentifier: bundleIdentifier)
                completion(.init(success: true, message: "Rule removed."))
            } catch {
                completion(.init(success: false, message: error.localizedDescription))
            }
        case .removeProcessRule:
            guard let processIdentifier = request.processIdentifier,
                  let bundleIdentifier = NSRunningApplication(processIdentifier: pid_t(processIdentifier))?.bundleIdentifier else {
                completion(.init(success: false, message: "The process does not have a resolvable macOS bundle identifier."))
                return
            }
            do {
                try ruleWriter.removeRule(bundleIdentifier: bundleIdentifier)
                completion(.init(success: true, message: "Rule removed.", bundleIdentifier: bundleIdentifier))
            } catch {
                completion(.init(success: false, message: error.localizedDescription))
            }
        }
    }

    private static func configurationResponse(_ result: Result<FilterConfigurationSnapshot, Error>) -> LimiterControlResponse {
        switch result {
        case .success(let configuration):
            .init(success: true, message: configuration.isEnabled ? "Filter enabled." : "Filter disabled.", configuration: configuration)
        case .failure(let error):
            .init(success: false, message: error.localizedDescription)
        }
    }
}
