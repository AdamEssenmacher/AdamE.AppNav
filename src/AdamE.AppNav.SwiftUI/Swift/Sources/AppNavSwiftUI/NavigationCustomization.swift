#if canImport(UIKit)
import SwiftUI

/// Native navigation style requested for an entry.
public enum NavigationDestinationTransition {
    case automatic
    case zoom(sourceID: String, namespace: Namespace.ID)

    case native(NativeStyle)

    /// A SwiftUI style for the destination. Use the dedicated zoom case for matched sources.
    /// Set `requiresMatchedSource` when an erased style needs a source key and exact mounted predecessor.
    @available(iOS 18, *)
    @MainActor
    public static func native(_ style: some NavigationTransition, requiresMatchedSource: Bool = false) -> Self {
        .native(NativeStyle(
            value: style, requiresSource: requiresMatchedSource || style is ZoomNavigationTransition
        ))
    }

    public struct NativeStyle {
        let value: Any
        let requiresSource: Bool
    }
}

/// How SwiftUI presents one AppNav modal stack.
public enum NavigationModalPresentation: Sendable {
    case sheet
    case fullScreenCover
}

/// Optional native destination decoration and animation policy. The app supplies any matching source views.
@MainActor
public struct NavigationCustomization {
    let modalPresentation: (ModalProjection) -> NavigationModalPresentation
    let transactionAnimation: ((NavigationTransitionContext) -> Animation?)?
    let transition: (NavigationDestinationContext) -> NavigationDestinationTransition
    let chrome: ((NavigationDestinationContext, AnyView) -> AnyView)?

    /// A verified single push, single pop, or isolated sheet change can use `transactionAnimation`.
    /// Returning nil installs the target immediately. Omit the policy to use `animatesSinglePushes` for pushes.
    public init(
        modalPresentation: @escaping (ModalProjection) -> NavigationModalPresentation = { _ in .sheet },
        transactionAnimation: ((NavigationTransitionContext) -> Animation?)? = nil,
        transition: @escaping (NavigationDestinationContext) -> NavigationDestinationTransition = { _ in .automatic },
        chrome: ((NavigationDestinationContext, AnyView) -> AnyView)? = nil
    ) {
        self.modalPresentation = modalPresentation
        self.transactionAnimation = transactionAnimation
        self.transition = transition
        self.chrome = chrome
    }
}
#endif
