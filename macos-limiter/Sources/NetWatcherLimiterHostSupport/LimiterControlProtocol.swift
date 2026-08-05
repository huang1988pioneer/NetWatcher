import Foundation
#if SWIFT_PACKAGE
import NetWatcherLimiterCore
#endif

public enum LimiterControlCommand: String, Codable, Sendable {
    case status
    case setEnabled
    case replaceRules
    case upsertRule
    case upsertProcessRule
    case removeRule
    case removeProcessRule
}

public struct LimiterControlRequest: Codable, Sendable {
    public var command: LimiterControlCommand
    public var enabled: Bool?
    public var rules: RuleSet?
    public var rule: LimitRule?
    public var bundleIdentifier: String?
    public var processIdentifier: Int?
    public var inboundBytesPerSecond: Int?
    public var outboundBytesPerSecond: Int?
    public var blockConnections: Bool?

    public init(command: LimiterControlCommand, enabled: Bool? = nil, rules: RuleSet? = nil, rule: LimitRule? = nil, bundleIdentifier: String? = nil, processIdentifier: Int? = nil, inboundBytesPerSecond: Int? = nil, outboundBytesPerSecond: Int? = nil, blockConnections: Bool? = nil) {
        self.command = command
        self.enabled = enabled
        self.rules = rules
        self.rule = rule
        self.bundleIdentifier = bundleIdentifier
        self.processIdentifier = processIdentifier
        self.inboundBytesPerSecond = inboundBytesPerSecond
        self.outboundBytesPerSecond = outboundBytesPerSecond
        self.blockConnections = blockConnections
    }
}

public struct LimiterControlResponse: Codable, Sendable {
    public var success: Bool
    public var message: String
    public var configuration: FilterConfigurationSnapshot?
    public var bundleIdentifier: String?

    public init(success: Bool, message: String, configuration: FilterConfigurationSnapshot? = nil, bundleIdentifier: String? = nil) {
        self.success = success
        self.message = message
        self.configuration = configuration
        self.bundleIdentifier = bundleIdentifier
    }
}
