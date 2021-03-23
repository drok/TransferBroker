using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// General Information about an assembly is controlled through the following
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyCompany("")]
[assembly: AssemblyCopyright("Copyright © 2021")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// Setting ComVisible to false makes the types in this assembly not visible
// to COM components.  If you need to access a type in this assembly from
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// Version information for an assembly consists of two values:
// 1. AssemblyVersion describes the version of the interface (ie, other mods
// will find the same consistent interface/library version) if they need to
// interface with this mod.
//
// 2. AssemblyFileVersion describes the implementation number. Ie, when bugs
// are fixed, the implementation number changes (new revision number), but
// AssemblyVersion remains the same (ie, unchanged interface)
// In Beta mode, assume each build changes the interface.

#if LABS || DEBUG || EXPERIMENTAL
[assembly: AssemblyVersion("0.4.1.*")]
#else
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion(TransferBroker.Versioning.MyFileVersion)]
#endif
