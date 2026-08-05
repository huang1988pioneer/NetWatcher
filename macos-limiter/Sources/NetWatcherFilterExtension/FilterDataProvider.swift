import Foundation
@preconcurrency import NetworkExtension
#if SWIFT_PACKAGE
import NetWatcherLimiterCore
#endif

public final class FilterDataProvider: NEFilterDataProvider, @unchecked Sendable {
    private let limiter = FlowLimiter()
    private lazy var ruleStore = RuleStore(url: rulesURL())

    public override func startFilter(completionHandler: @escaping ((any Error)?) -> Void) {
        let callback = ErrorCallback(completionHandler)
        let settings = NEFilterSettings(rules: [], defaultAction: .filterData)
        apply(settings) { error in
            callback.call(error)
        }
    }

    public override func stopFilter(with reason: NEProviderStopReason, completionHandler: @escaping () -> Void) {
        completionHandler()
    }

    public override func handleNewFlow(_ flow: NEFilterFlow) -> NEFilterNewFlowVerdict {
        let appIdentifier = FlowAppIdentity.identifier(for: flow)
        if let rule = ruleStore.load().rule(for: appIdentifier), rule.isEnabled, rule.blockConnections {
            return .drop()
        }

        return NEFilterNewFlowVerdict.filterDataVerdict(
            withFilterInbound: true,
            peekInboundBytes: 16 * 1024,
            filterOutbound: true,
            peekOutboundBytes: 16 * 1024)
    }

    public override func handleInboundData(
        from flow: NEFilterFlow,
        readBytesStartOffset offset: Int,
        readBytes: Data
    ) -> NEFilterDataVerdict {
        verdict(for: flow, direction: .inbound, bytes: readBytes.count)
    }

    public override func handleOutboundData(
        from flow: NEFilterFlow,
        readBytesStartOffset offset: Int,
        readBytes: Data
    ) -> NEFilterDataVerdict {
        verdict(for: flow, direction: .outbound, bytes: readBytes.count)
    }

    private func verdict(for flow: NEFilterFlow, direction: LimitDirection, bytes: Int) -> NEFilterDataVerdict {
        let rules = ruleStore.load()
        let decision = limiter.allowedBytes(
            appIdentifier: FlowAppIdentity.identifier(for: flow),
            direction: direction,
            requestedBytes: bytes,
            rules: rules)

        switch decision {
        case .unlimited:
            return NEFilterDataVerdict(passBytes: bytes, peekBytes: 16 * 1024)
        case .pass(let allowedBytes):
            return NEFilterDataVerdict(passBytes: allowedBytes, peekBytes: 16 * 1024)
        case .pause(let seconds):
            DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + seconds) { [weak self, weak flow] in
                guard let self, let flow else {
                    return
                }

                self.resumeFlow(flow, with: NEFilterDataVerdict(passBytes: 16 * 1024, peekBytes: 16 * 1024))
            }
            return .pause()
        }
    }

    private func rulesURL() -> URL {
        if let configuredPath = filterConfiguration
            .vendorConfiguration?["RulesPath"] as? String,
           !configuredPath.isEmpty {
            return URL(fileURLWithPath: configuredPath)
        }

        return LimiterPaths.rulesURL()
    }
}

private final class ErrorCallback: @unchecked Sendable {
    private let completion: ((any Error)?) -> Void

    init(_ completion: @escaping ((any Error)?) -> Void) {
        self.completion = completion
    }

    func call(_ error: ((any Error)?)) {
        completion(error)
    }
}
