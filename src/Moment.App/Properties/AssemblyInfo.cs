using System.Runtime.CompilerServices;

// The test assembly keeps its Moment.* compatibility identity even though the
// shipped executable assembly is named Hourbit.
[assembly: InternalsVisibleTo("Moment.App.Tests")]
