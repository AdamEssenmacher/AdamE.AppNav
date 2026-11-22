// swift-tools-version:6.0
import Foundation
import PackageDescription

let environment = ProcessInfo.processInfo.environment
let packages = environment["NUGET_PACKAGES"].flatMap { $0.isEmpty ? nil : URL(fileURLWithPath: $0) } ??
    FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".nuget/packages")
let runtime = environment["OAHU_RUNTIME_SWIFT"].flatMap { $0.isEmpty ? nil : $0 } ??
    packages.appendingPathComponent("oahu.ios/0.1.0-preview.g7a56b3ba/swift/OahuRuntime").path

// The UIKit and OAHU parts build for iOS only. macOS is listed so `swift test` can run the navigation
// state machine's tests on the host.
let package = Package(
    name: "AppNavSwiftUI",
    platforms: [.iOS(.v17), .macOS(.v15)],
    products: [.library(name: "AppNavSwiftUI", targets: ["AppNavSwiftUI"])],
    dependencies: [.package(name: "OahuRuntime", path: runtime)],
    targets: [
        .target(name: "AppNavSwiftUI", dependencies: [
            .product(name: "OahuRuntime", package: "OahuRuntime", condition: .when(platforms: [.iOS]))
        ]),
        .testTarget(name: "AppNavSwiftUITests", dependencies: ["AppNavSwiftUI"]),
    ]
)
