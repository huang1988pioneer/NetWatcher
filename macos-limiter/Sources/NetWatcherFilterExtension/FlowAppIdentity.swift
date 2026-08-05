import Foundation
@preconcurrency import NetworkExtension
import Security

enum FlowAppIdentity {
    static func identifier(for flow: NEFilterFlow) -> String? {
        if let appToken = flow.sourceAppAuditToken, !appToken.isEmpty {
            return AuditTokenAppResolver.bundleIdentifier(for: appToken)
                ?? auditIdentifier(prefix: "audit", token: appToken)
        }

        if #available(macOS 13.0, *),
           let processToken = flow.sourceProcessAuditToken,
           !processToken.isEmpty {
            return AuditTokenAppResolver.bundleIdentifier(for: processToken)
                ?? auditIdentifier(prefix: "process-audit", token: processToken)
        }

        return nil
    }

    private static func auditIdentifier(prefix: String, token: Data) -> String {
        prefix + ":" + token.map { String(format: "%02x", $0) }.joined()
    }
}

private enum AuditTokenAppResolver {
    static func bundleIdentifier(for auditToken: Data) -> String? {
        let attributes = [kSecGuestAttributeAudit as String: auditToken as CFData] as CFDictionary
        var code: SecCode?
        guard SecCodeCopyGuestWithAttributes(nil, attributes, SecCSFlags(), &code) == errSecSuccess,
              let code else {
            return nil
        }

        var staticCode: SecStaticCode?
        guard SecCodeCopyStaticCode(code, SecCSFlags(), &staticCode) == errSecSuccess,
              let staticCode else {
            return nil
        }

        var information: CFDictionary?
        guard SecCodeCopySigningInformation(staticCode, SecCSFlags(), &information) == errSecSuccess,
              let signingInformation = information as? [String: Any] else {
            return nil
        }

        return signingInformation[kSecCodeInfoIdentifier as String] as? String
    }
}
