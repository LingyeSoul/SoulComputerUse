using System.Runtime.CompilerServices;

// The headless starter's contract cases take a per-run lock name and read it back, so they can prove
// that a refused start took nothing. That needs the named lock entry points, which stay internal so
// production code can only ever take the contract-pinned name.
[assembly: InternalsVisibleTo("SoulComputerUse.ContractTests")]
