// swift-tools-version:6.0
import Foundation
import PackageDescription

let runtime = ProcessInfo.processInfo.environment["OAHU_RUNTIME_SWIFT"] ??
    FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent(".nuget/packages/oahu.ios/0.1.0-preview.g7a56b3ba/swift/OahuRuntime").path

let package = Package(
    name: "AppNavSwiftUI",
    platforms: [.iOS(.v18)],
    products: [.library(name: "AppNavSwiftUI", targets: ["AppNavSwiftUI"])],
    dependencies: [.package(name: "OahuRuntime", path: runtime)],
    targets: [.target(name: "AppNavSwiftUI", dependencies: [.product(name: "OahuRuntime", package: "OahuRuntime")])]
)
