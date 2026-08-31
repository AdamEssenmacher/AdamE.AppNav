# AppNav documentation

AdamE.AppNav is a route-first navigation framework with a host-independent core
and a production .NET MAUI adapter. Choose the path that matches what you are
trying to do.

## Evaluate AppNav

- Read the repository [overview and quickstart](../README.md).
- Review [topology and planning](concepts/topology-and-planning.md) to understand
  stacks, independent branches, modals, and canonical navigation.
- Review the [preview release notes](release-notes/index.md) and supported
  limits before adopting the preview.

## Build your first app

1. Follow [Getting started](guides/getting-started.md).
2. Learn [routing and metadata](concepts/routing-and-metadata.md).
3. Continue with [MAUI integration](guides/maui-integration.md).

## Add advanced capabilities

- [External navigation](guides/external-navigation.md): trusted app-link, push,
  QR, and provider ingress.
- [Deferred navigation](guides/deferred-navigation.md): durable auth defer and
  replay.
- [Requests and provenance](concepts/requests-and-provenance.md): choose the
  correct request abstraction and preserve runtime context.
- [Route-owned presentation pages](advanced/route-owned-presentation-pages.md):
  native subpages that remain one logical route.

## Debug an integration

- Start with [Troubleshooting](guides/troubleshooting.md).
- Use the [diagnostics reference](reference/diagnostics.md).
- Resolve build-time problems with the
  [source-generator diagnostics](reference/source-generator-diagnostics.md).

## Write another adapter

- Read the [adapter contract](advanced/adapter-contract.md).
- Use the host-independent [topology model](concepts/topology-and-planning.md).

## Maintain and release AppNav

These documents are for repository maintainers, not application onboarding:

- [Testing](maintainers/testing.md)
- [Public preview release checklist](maintainers/release-checklist.md)
- [AppRouteRequest dogfood checkpoint](maintainers/app-route-request-dogfood-checkpoint.md)

## All documents

Every documentation page is indexed here:

- [Getting started](guides/getting-started.md)
- [MAUI integration](guides/maui-integration.md)
- [External navigation](guides/external-navigation.md)
- [Deferred navigation](guides/deferred-navigation.md)
- [Troubleshooting](guides/troubleshooting.md)
- [Routing and metadata](concepts/routing-and-metadata.md)
- [Topology and planning](concepts/topology-and-planning.md)
- [Requests and provenance](concepts/requests-and-provenance.md)
- [Route-owned presentation pages](advanced/route-owned-presentation-pages.md)
- [Adapter contract](advanced/adapter-contract.md)
- [Diagnostics](reference/diagnostics.md)
- [Source-generator diagnostics](reference/source-generator-diagnostics.md)
- [Testing](maintainers/testing.md)
- [Public preview release checklist](maintainers/release-checklist.md)
- [AppRouteRequest dogfood checkpoint](maintainers/app-route-request-dogfood-checkpoint.md)
- [Release notes](release-notes/index.md)
- [0.1.0-preview.1 release notes](release-notes/0.1.0-preview.1.md)

Return to the [repository README](../README.md).
