import Foundation

public struct LimitRule: Codable, Equatable, Sendable {
    public var bundleIdentifier: String
    public var inboundBytesPerSecond: Int?
    public var outboundBytesPerSecond: Int?
    public var isEnabled: Bool
    public var blockConnections: Bool

    public init(
        bundleIdentifier: String,
        inboundBytesPerSecond: Int? = nil,
        outboundBytesPerSecond: Int? = nil,
        isEnabled: Bool = true,
        blockConnections: Bool = false
    ) {
        self.bundleIdentifier = bundleIdentifier
        self.inboundBytesPerSecond = inboundBytesPerSecond
        self.outboundBytesPerSecond = outboundBytesPerSecond
        self.isEnabled = isEnabled
        self.blockConnections = blockConnections
    }

    public func limit(for direction: LimitDirection) -> Int? {
        guard isEnabled else {
            return nil
        }

        return switch direction {
        case .inbound:
            inboundBytesPerSecond
        case .outbound:
            outboundBytesPerSecond
        }
    }
}
