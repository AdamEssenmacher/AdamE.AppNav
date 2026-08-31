# AppNav documentation

AdamE.AppNav is a route-first navigation framework with a host-independent core
and a production .NET MAUI adapter. Choose the path that matches what you are
trying to do.

## Evaluate AppNav

- Start with the repository [overview](../README.md), including the shortest
  source-sample path and preview limits.
- Read [Why AppNav?](concepts/00-why-appnav.md) to understand the design
  tradeoffs and decide whether the route-first model fits your application.
- Keep the [Glossary](reference/glossary.md) nearby for precise definitions and
  distinctions such as route versus page, plan versus presentation, and source
  versus provenance.
- Confirm the [one-navigation-owner
  boundary](guides/02-maui-integration.md#one-navigation-owner-no-shell-or-prism-navigation):
  AppNav does not support Shell routing, Prism navigation, or mixed ownership
  of the same window.
- Review [topology and planning](concepts/02-topology-and-planning.md) to understand
  stacks, independent branches, modals, and canonical navigation.
- Review the [preview release notes](release-notes/index.md) and supported
  limits before adopting the preview.

## Build your first app

The Getting Started guide begins with a minimal buildable sample. When that
sample needs a richer setting, the concept guides use **Glyphmere**, a
fictional RPG pause menu, to explain independent branch histories, requests,
and provenance.

1. Run the source sample with [Getting started](guides/01-getting-started.md).
2. Deepen the sample's model with [routing and
   metadata](concepts/01-routing-and-metadata.md).
3. Understand independent branches and canonical shapes with
   [topology and planning](concepts/02-topology-and-planning.md).
4. Choose the right request abstraction with
   [requests and provenance](concepts/03-requests-and-provenance.md).
5. Continue with [MAUI integration](guides/02-maui-integration.md).
6. Structure inner application code with
   [Application architecture and testing](guides/03-application-architecture-and-testing.md).
7. Handle completion, Back, cancellation, and failures with
   [Navigation outcomes and failure handling](guides/04-navigation-outcomes-and-failure-handling.md).

## Structure and test your application

- Keep routes, topology, policies, and presentation logic that requests typed
  destinations in ordinary .NET code with [Application architecture and
  testing](guides/03-application-architecture-and-testing.md).
- Use [MAUI integration](guides/02-maui-integration.md) to connect that inner
  model to pages, lifecycle, native presentation, and platform services.
- Treat the [adapter contract](advanced/adapter-contract.md) as advanced
  material when implementing a non-MAUI host.

## Add advanced capabilities

- [Navigation outcomes and failure handling](guides/04-navigation-outcomes-and-failure-handling.md):
  successful results, unhandled Back, exceptions, cancellation, and commit semantics.
- [External navigation](guides/05-external-navigation.md): trusted app-link, push,
  QR, and provider ingress.
- [Deferred navigation](guides/06-deferred-navigation.md): durable auth defer and
  replay.
- [Requests and provenance](concepts/03-requests-and-provenance.md): choose the
  correct request abstraction and preserve runtime context.
- [Route-owned presentation pages](advanced/route-owned-presentation-pages.md):
  native subpages that remain one logical route.

## Debug an integration

- Start with [Troubleshooting](guides/07-troubleshooting.md).
- Configure and interpret [logging, tracing, and
  diagnostics](reference/diagnostics.md).
- Check terminology in the [Glossary](reference/glossary.md).
- Resolve build-time problems with the
  [source-generator diagnostics](reference/source-generator-diagnostics.md).

## Write another adapter

- Read the [adapter contract](advanced/adapter-contract.md).
- Use the host-independent [topology model](concepts/02-topology-and-planning.md).

## Maintain and release AppNav

These documents are for repository maintainers, not application onboarding:

- [Testing](maintainers/testing.md)
- [Public preview release checklist](maintainers/release-checklist.md)
- [AppRouteRequest dogfood checkpoint](maintainers/app-route-request-dogfood-checkpoint.md)

## All documents

Every documentation page is indexed here:

- [Getting started](guides/01-getting-started.md)
- [MAUI integration](guides/02-maui-integration.md)
- [Application architecture and testing](guides/03-application-architecture-and-testing.md)
- [Navigation outcomes and failure handling](guides/04-navigation-outcomes-and-failure-handling.md)
- [External navigation](guides/05-external-navigation.md)
- [Deferred navigation](guides/06-deferred-navigation.md)
- [Troubleshooting](guides/07-troubleshooting.md)
- [Why AppNav?](concepts/00-why-appnav.md)
- [Routing and metadata](concepts/01-routing-and-metadata.md)
- [Topology and planning](concepts/02-topology-and-planning.md)
- [Requests and provenance](concepts/03-requests-and-provenance.md)
- [Route-owned presentation pages](advanced/route-owned-presentation-pages.md)
- [Adapter contract](advanced/adapter-contract.md)
- [Logging, tracing, and diagnostics](reference/diagnostics.md)
- [Glossary](reference/glossary.md)
- [Source-generator diagnostics](reference/source-generator-diagnostics.md)
- [Testing](maintainers/testing.md)
- [Public preview release checklist](maintainers/release-checklist.md)
- [AppRouteRequest dogfood checkpoint](maintainers/app-route-request-dogfood-checkpoint.md)
- [Release notes](release-notes/index.md)
- [0.1.0-preview.1 release notes](release-notes/0.1.0-preview.1.md)

Return to the [repository README](../README.md).
