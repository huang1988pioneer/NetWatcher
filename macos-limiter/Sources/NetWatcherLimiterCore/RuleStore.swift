import Foundation

public final class RuleStore: @unchecked Sendable {
    private let url: URL
    private let lock = NSLock()
    private var cachedRules = RuleSet()
    private var cachedModificationDate: Date?

    public init(url: URL) {
        self.url = url
    }

    public func load() -> RuleSet {
        lock.lock()
        defer { lock.unlock() }

        do {
            let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
            let modificationDate = attributes[.modificationDate] as? Date
            if modificationDate == cachedModificationDate {
                return cachedRules
            }

            let data = try Data(contentsOf: url)
            cachedRules = try JSONDecoder().decode(RuleSet.self, from: data)
            cachedModificationDate = modificationDate
            return cachedRules
        } catch {
            cachedRules = RuleSet()
            cachedModificationDate = nil
            return cachedRules
        }
    }

    public func save(_ rules: RuleSet) throws {
        lock.lock()
        defer { lock.unlock() }

        try FileManager.default.createDirectory(
            at: url.deletingLastPathComponent(),
            withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        let data = try encoder.encode(rules)
        try data.write(to: url, options: [.atomic])
        cachedRules = rules
        cachedModificationDate = try FileManager.default
            .attributesOfItem(atPath: url.path)[.modificationDate] as? Date
    }
}
