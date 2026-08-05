import Foundation

public enum LimiterPaths {
    public static let defaultAppGroupIdentifier = "group.com.huang1988pioneer.netwatcher"
    public static let filterDataProviderBundleIdentifier = "com.huang1988pioneer.netwatcher.limiter.filter"
    public static let rulesDirectoryName = "NetWatcherLimiter"
    public static let rulesFileName = "rules.json"

    public static func sharedContainerURL(
        appGroupIdentifier: String = defaultAppGroupIdentifier,
        fallbackURL: URL = FileManager.default.temporaryDirectory
    ) -> URL {
        FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: appGroupIdentifier) ?? fallbackURL
    }

    public static func rulesURL(
        appGroupIdentifier: String = defaultAppGroupIdentifier,
        fallbackURL: URL = FileManager.default.temporaryDirectory
    ) -> URL {
        sharedContainerURL(appGroupIdentifier: appGroupIdentifier, fallbackURL: fallbackURL)
            .appendingPathComponent(rulesDirectoryName, isDirectory: true)
            .appendingPathComponent(rulesFileName)
    }
}
