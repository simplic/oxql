using System.Runtime.CompilerServices;
using OxQL.Core.Attributes;

// Entity classes compiled against a 1.x version of this assembly reference the attribute here.
// It lives in OxQL.Model, next to the scan that reads it, and the forward keeps those
// references binding to that one type.
[assembly: TypeForwardedTo(typeof(OxQLTypeAttribute))]
