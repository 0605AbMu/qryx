// Tests mutate the process-wide QryxOptions.Global (AddQryx assigns it), so they must not run in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
