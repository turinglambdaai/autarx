import Foundation
import RivetUpdate

/// Online update service for the macOS workbench (shared/spec/UPDATE.md) —
/// a thin product adapter over rivet's `RivetUpdate` module (the family's
/// shared host-side update client since rivet 0.8.1, the taskly ba3a54e
/// pattern): this file only contributes the product facts (feed, signing
/// key, key id, bundle name) and the error surface. Signed-manifest
/// verification, the bounded delegate-driven download, size/SHA-256
/// enforcement, the ditto unpack + version confirmation, the atomic swap
/// with rollback, and the sheet-safe quit/relaunch handoff all live in the
/// library now (the hand-rolled CryptoKit/bash client is retired).
///
/// Only a copy under /Applications (or ~/Applications) can self-update —
/// debug builds and random folders get the "not installed" answer.
@MainActor
public final class UpdateService {
    /// Ed25519 public key (base64, raw 32 bytes) — pair of the
    /// UPDATE_ED25519_PRIVATE_KEY_B64 GitHub secret that signs BOTH release
    /// feeds: this workbench wrapper and the CLI channel's
    /// update-manifest.json, which the Racket updater authenticates with
    /// the same key in SubjectPublicKeyInfo form (racket/autarx/update.rkt).
    /// Generate: scripts/update-keys.sh.
    nonisolated public static let publicKeyBase64 = "Ijm2Iqrres1BbLnx5DQCvZtbbKb4jBKRpUbGSQ9FWxM="

    /// The manifest must carry this key id — rotation ships a build that
    /// trusts the next key before releases stop being signed with this one.
    nonisolated public static let expectedKeyID = "autarx-2026-10"

    public static let manifestURL = URL(string:
        "https://github.com/turlamai/autarx/releases/latest/download/update-app-manifest.json")!
    public static let releasesPage = "https://github.com/turlamai/autarx/releases/latest"

    public enum UpdateError: LocalizedError {
        case notInstalled
        case badPublicKey
        case manifestMissing
        case signatureInvalid
        case checksumMismatch
        case versionMismatch(expected: String, got: String)
        case network(String)

        public var errorDescription: String? {
            switch self {
            case .notInstalled: return "not an installed app bundle"
            case .badPublicKey: return "embedded public key is invalid"
            case .manifestMissing: return "release has no update manifest"
            case .signatureInvalid: return "manifest signature check failed"
            case .checksumMismatch: return "downloaded archive failed the sha256 check"
            case .versionMismatch(let expected, let got): return "bundle version \(got) ≠ manifest \(expected)"
            case .network(let message): return message
            }
        }
    }

    public typealias Manifest = RivetUpdateManifest

    public enum CheckResult {
        case upToDate
        case available(Manifest)
    }

    /// nil only when the (constant) product configuration failed the
    /// library's fail-closed validation — surfaced as badPublicKey.
    private let service: RivetUpdateService?

    init(feedURL: URL? = nil, bundleURL: URL? = nil,
                currentVersion: String? = nil,
                sessionConfiguration: URLSessionConfiguration? = nil,
                keyOverride: String? = nil) {
        let configuration = RivetUpdateConfiguration(
            feedURL: feedURL ?? Self.manifestURL,
            publicKeyBase64: keyOverride ?? Self.publicKeyBase64,
            keyID: Self.expectedKeyID,
            bundleName: "Autarx.app")
        self.service = try? RivetUpdateService(
            configuration: configuration,
            bundlePath: bundleURL?.path,
            currentVersion: currentVersion,
            sessionConfiguration: sessionConfiguration)
    }

    /// Translates library errors into this product's error surface
    /// (English-only strings, matching the workbench's single-language UI).
    nonisolated private static func map(_ error: RivetUpdateError) -> UpdateError {
        switch error {
        case .notInstalled: return .notInstalled
        case .badPublicKey: return .badPublicKey
        case .invalidConfiguration: return .badPublicKey
        case .manifestMissing: return .manifestMissing
        case .signatureInvalid: return .signatureInvalid
        case .checksumMismatch: return .checksumMismatch
        case .sizeMismatch: return .checksumMismatch
        case .versionMismatch(let expected, let got):
            return .versionMismatch(expected: expected, got: got)
        case .network(let message): return .network(message)
        }
    }

    /// Removes swap work dirs older than 30 minutes — the swap script
    /// cannot reliably delete its own directory while bash is still
    /// reading it, so a later launch sweeps the leftovers.
    nonisolated public static func sweepStaleWorkDirs() {
        RivetUpdateService.cleanupStaleWorkDirectories(olderThan: 30 * 60)
    }

    // MARK: - Environment

    /// The installed bundle this copy can update in place, or nil for
    /// development copies (not under /Applications or ~/Applications).
    public var installedBundleURL: URL? { service?.installedBundleURL }

    public var canUpdate: Bool { service?.canUpdate ?? false }

    // MARK: - Check

    /// Fetches the latest release manifest, verifies its Ed25519 signature
    /// and compares versions. Throws with a precise reason otherwise
    /// (presentation — silent swallow vs error dialog — is the caller's job).
    public func check() async throws -> CheckResult {
        guard let service else { throw UpdateError.badPublicKey }
        do {
            switch try await service.check() {
            case .upToDate: return .upToDate
            case .available(let manifest): return .available(manifest)
            }
        } catch let error as RivetUpdateError {
            throw Self.map(error)
        }
    }

    // MARK: - Download + install

    /// Downloads, verifies (sha256, then unpacked bundle version), swaps the
    /// bundle in place and relaunches. Throws before touching the running
    /// install when anything is off. `onProgress` reports download percent
    /// (0…100) on a background thread for the update panel.
    public func downloadAndInstall(_ manifest: Manifest,
                                   onProgress: (@Sendable (Int) -> Void)? = nil) async throws {
        guard let service else { throw UpdateError.badPublicKey }
        do {
            try await service.downloadAndInstall(manifest, onProgress: onProgress)
        } catch let error as RivetUpdateError {
            throw Self.map(error)
        }
    }

    // MARK: - Version compare (numeric per segment; 1.2 > 1.1.9)

    nonisolated public static func isVersion(_ a: String, greaterThan b: String) -> Bool {
        RivetUpdateService.isVersion(a, greaterThan: b)
    }
}
