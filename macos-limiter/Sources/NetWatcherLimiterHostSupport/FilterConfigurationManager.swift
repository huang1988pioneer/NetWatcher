import Foundation
@preconcurrency import NetworkExtension
#if SWIFT_PACKAGE
import NetWatcherLimiterCore
#endif

public struct FilterConfigurationSnapshot: Codable, Equatable, Sendable {
    public var isEnabled: Bool
    public var localizedDescription: String?
    public var grade: String
    public var rulesPath: String

    public init(isEnabled: Bool, localizedDescription: String?, grade: String, rulesPath: String) {
        self.isEnabled = isEnabled
        self.localizedDescription = localizedDescription
        self.grade = grade
        self.rulesPath = rulesPath
    }
}

public enum FilterConfigurationVendorKeys {
    public static let rulesPath = "RulesPath"
}

public final class FilterConfigurationManager: @unchecked Sendable {
    public static let rulesPathVendorKey = FilterConfigurationVendorKeys.rulesPath

    private let appGroupIdentifier: String
    private let localizedDescription: String
    private let manager: NEFilterManager

    public init(
        appGroupIdentifier: String = LimiterPaths.defaultAppGroupIdentifier,
        localizedDescription: String = "NetWatcher Limiter",
        manager: NEFilterManager = .shared()
    ) {
        self.appGroupIdentifier = appGroupIdentifier
        self.localizedDescription = localizedDescription
        self.manager = manager
    }

    public var rulesURL: URL {
        LimiterPaths.rulesURL(appGroupIdentifier: appGroupIdentifier)
    }

    public func load(completion: @escaping (Result<FilterConfigurationSnapshot, Error>) -> Void) {
        let callback = ResultCallback(completion)
        manager.loadFromPreferences { [manager, rulesURL] error in
            if let error {
                callback.call(.failure(error))
                return
            }

            callback.call(.success(Self.snapshot(manager: manager, rulesURL: rulesURL)))
        }
    }

    public func setEnabled(
        _ isEnabled: Bool,
        completion: @escaping (Result<FilterConfigurationSnapshot, Error>) -> Void
    ) {
        let callback = ResultCallback(completion)
        manager.loadFromPreferences { [manager, localizedDescription, rulesURL] error in
            if let error {
                callback.call(.failure(error))
                return
            }

            let configuration = manager.providerConfiguration ?? NEFilterProviderConfiguration()
            configuration.filterSockets = true
            configuration.filterDataProviderBundleIdentifier = LimiterPaths.filterDataProviderBundleIdentifier
            configuration.vendorConfiguration = [
                Self.rulesPathVendorKey: rulesURL.path
            ]

            manager.localizedDescription = localizedDescription
            manager.providerConfiguration = configuration
            manager.grade = .firewall
            manager.isEnabled = isEnabled

            manager.saveToPreferences { error in
                if let error {
                    callback.call(.failure(error))
                    return
                }

                callback.call(.success(Self.snapshot(manager: manager, rulesURL: rulesURL)))
            }
        }
    }

    public func remove(completion: @escaping (Result<Void, Error>) -> Void) {
        let callback = ResultCallback(completion)
        manager.loadFromPreferences { [manager] error in
            if let error {
                callback.call(.failure(error))
                return
            }

            manager.removeFromPreferences { error in
                if let error {
                    callback.call(.failure(error))
                    return
                }

                callback.call(.success(()))
            }
        }
    }

    private static func snapshot(manager: NEFilterManager, rulesURL: URL) -> FilterConfigurationSnapshot {
        FilterConfigurationSnapshot(
            isEnabled: manager.isEnabled,
            localizedDescription: manager.localizedDescription,
            grade: manager.grade == .firewall ? "firewall" : "inspector",
            rulesPath: rulesURL.path)
    }
}

private final class ResultCallback<Value>: @unchecked Sendable {
    private let completion: (Result<Value, Error>) -> Void

    init(_ completion: @escaping (Result<Value, Error>) -> Void) {
        self.completion = completion
    }

    func call(_ result: Result<Value, Error>) {
        completion(result)
    }
}
