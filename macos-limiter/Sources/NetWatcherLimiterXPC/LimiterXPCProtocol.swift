import Foundation

@objc(NetWatcherLimiterXPCProtocol)
public protocol LimiterXPCProtocol {
    func setFilterEnabled(_ enabled: Bool, withReply reply: @escaping (Bool, String?) -> Void)
    func replaceRules(_ jsonData: Data, withReply reply: @escaping (Bool, String?) -> Void)
    func upsertRule(_ jsonData: Data, withReply reply: @escaping (Bool, String?) -> Void)
    func removeRule(_ bundleIdentifier: String, withReply reply: @escaping (Bool, String?) -> Void)
    func status(withReply reply: @escaping (Data?, String?) -> Void)
}
