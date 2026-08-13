using System.Runtime.CompilerServices;

// The test assembly keeps its Hourbit.* compatibility identity even though the
// shipped executable assembly is named Hourbit.
[assembly: InternalsVisibleTo("Hourbit.App.Tests")]
