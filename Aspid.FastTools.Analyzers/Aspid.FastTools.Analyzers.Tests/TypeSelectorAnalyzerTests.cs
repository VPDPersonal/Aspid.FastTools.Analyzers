using System.Threading.Tasks;
using Xunit;
using VerifyCS = Microsoft.CodeAnalysis.CSharp.Testing.CSharpAnalyzerVerifier<
    Aspid.FastTools.Analyzers.AspidFastToolsAnalyzer,
    Microsoft.CodeAnalysis.Testing.Verifiers.XUnitVerifier>;

namespace Aspid.FastTools.Analyzers.Tests;

public class TypeSelectorAnalyzerTests
{
    // Minimal stand-ins for the attributes the analyzer matches by full name, so the tests need no Unity / package
    // references. Appended after the test snippet (which carries the `using` directives) to keep usings at file top.
    private const string Stubs = @"
namespace UnityEngine
{
    public class Object { }
    public sealed class SerializeReference : System.Attribute { }
}
namespace Aspid.FastTools.Types
{
    [System.Flags] public enum TypeAllow { None = 0, Abstract = 1, Interface = 2, All = 3 }

    public sealed class TypeSelectorAttribute : System.Attribute
    {
        public TypeSelectorAttribute() { }
        public TypeSelectorAttribute(System.Type type) { }
        public TypeSelectorAttribute(params System.Type[] types) { }
        public TypeAllow Allow { get; set; }
    }
}";

    private static Task Verify(string code) => VerifyCS.VerifyAnalyzerAsync(code + "\n" + Stubs);

    [Fact]
    public Task StringField_TypeNamePicker_NoDiagnostic() => Verify(@"
using Aspid.FastTools.Types;
class C { [TypeSelector(typeof(System.Object))] private string _type; }");

    [Fact]
    public Task ManagedReferenceField_NoDiagnostic() => Verify(@"
using UnityEngine;
using Aspid.FastTools.Types;
interface IFoo { }
class FooImpl : IFoo { }
class C { [SerializeReference, TypeSelector] private IFoo _foo; }");

    [Fact]
    public Task ManagedReferenceList_NoDiagnostic() => Verify(@"
using UnityEngine;
using Aspid.FastTools.Types;
using System.Collections.Generic;
interface IFoo { }
class FooImpl : IFoo { }
class C { [SerializeReference, TypeSelector] private List<IFoo> _foos; }");

    [Fact]
    public Task UnsupportedFieldType_ReportsAFT0001() => Verify(@"
using Aspid.FastTools.Types;
class C { [{|AFT0001:TypeSelector|}] private int _value; }");

    [Fact]
    public Task AllowOnManagedReference_ReportsAFT0002() => Verify(@"
using UnityEngine;
using Aspid.FastTools.Types;
interface IFoo { }
class FooImpl : IFoo { }
class C { [SerializeReference, TypeSelector({|AFT0002:Allow = TypeAllow.Interface|})] private IFoo _foo; }");

    [Fact]
    public Task AllowNoneOnManagedReference_NoDiagnostic() => Verify(@"
using UnityEngine;
using Aspid.FastTools.Types;
interface IFoo { }
class FooImpl : IFoo { }
class C { [SerializeReference, TypeSelector(Allow = TypeAllow.None)] private IFoo _foo; }");

    [Fact]
    public Task AllowOnStringField_NoDiagnostic() => Verify(@"
using Aspid.FastTools.Types;
class C { [TypeSelector(Allow = TypeAllow.Interface)] private string _type; }");

    [Fact]
    public Task DisjointBaseType_ReportsAFT0003() => Verify(@"
using UnityEngine;
using Aspid.FastTools.Types;
class Base { }
class Unrelated { }
class C { [SerializeReference, TypeSelector({|AFT0003:typeof(Unrelated)|})] private Base _value; }");

    [Fact]
    public Task AssignableBaseType_NoDiagnostic() => Verify(@"
using UnityEngine;
using Aspid.FastTools.Types;
class Base { }
class Derived : Base { }
class C { [SerializeReference, TypeSelector(typeof(Derived))] private Base _value; }");

    [Fact]
    public Task InterfaceBaseType_ImplDerivesFromBoth_NoDiagnostic() => Verify(@"
using UnityEngine;
using Aspid.FastTools.Types;
interface IMarker { }
class Base { }
class MarkerImpl : Base, IMarker { }
class C { [SerializeReference, TypeSelector(typeof(IMarker))] private Base _value; }");

    // The only IMarker implementation does not derive from Base, so the intersection is empty.
    // AFT0003 must NOT fire: an interface paired with a non-sealed class is never provably disjoint.
    [Fact]
    public Task InterfaceBaseType_ImplNotDerivingFromFieldType_ReportsAFT0005() => Verify(@"
using UnityEngine;
using Aspid.FastTools.Types;
interface IMarker { }
class Base { }
class MarkerImpl : IMarker { }
class C { [SerializeReference, {|AFT0005:TypeSelector(typeof(IMarker))|}] private Base _value; }");

    [Fact]
    public Task InterfaceBaseType_SealedFieldTypeNotImplementing_ReportsAFT0003() => Verify(@"
using UnityEngine;
using Aspid.FastTools.Types;
interface IMarker { }
sealed class Leaf { }
class C { [SerializeReference, TypeSelector({|AFT0003:typeof(IMarker)|})] private Leaf _value; }");

    [Fact]
    public Task InterfaceBaseType_SealedFieldTypeImplementing_NoDiagnostic() => Verify(@"
using UnityEngine;
using Aspid.FastTools.Types;
interface IMarker { }
sealed class Leaf : IMarker { }
class C { [SerializeReference, TypeSelector(typeof(IMarker))] private Leaf _value; }");

    [Fact]
    public Task SealedBaseTypeNotImplementingInterfaceField_ReportsAFT0003() => Verify(@"
using UnityEngine;
using Aspid.FastTools.Types;
interface IWeapon { }
sealed class Unrelated { }
class C { [SerializeReference, TypeSelector({|AFT0003:typeof(Unrelated)|})] private IWeapon _value; }");

    // AFT0004 — managed reference to a UnityEngine.Object-derived type

    [Fact]
    public Task ObjectDerivedManagedReference_ReportsAFT0004() => Verify(@"
using UnityEngine;
using Aspid.FastTools.Types;
class MyComponent : Object { }
class C { [SerializeReference, {|AFT0004:TypeSelector|}] private MyComponent _comp; }");

    [Fact]
    public Task InterfaceManagedReference_NoAFT0004() => Verify(@"
using UnityEngine;
using Aspid.FastTools.Types;
interface IFoo { }
class Impl : IFoo { }
class C { [SerializeReference, TypeSelector] private IFoo _foo; }");

    // AFT0005 — no visible concrete implementation

    [Fact]
    public Task InterfaceWithNoImplementations_ReportsAFT0005() => Verify(@"
using UnityEngine;
using Aspid.FastTools.Types;
interface IEmpty { }
class C { [SerializeReference, {|AFT0005:TypeSelector|}] private IEmpty _empty; }");

    [Fact]
    public Task InterfaceWithOneConcreteImpl_NoAFT0005() => Verify(@"
using UnityEngine;
using Aspid.FastTools.Types;
interface IFoo { }
class FooImpl : IFoo { }
class C { [SerializeReference, TypeSelector] private IFoo _foo; }");

    [Fact]
    public Task AbstractBaseWithOnlyAbstractSubclasses_ReportsAFT0005() => Verify(@"
using UnityEngine;
using Aspid.FastTools.Types;
abstract class Animal { }
abstract class Mammal : Animal { }
class C { [SerializeReference, {|AFT0005:TypeSelector|}] private Animal _animal; }");

    [Fact]
    public Task ConcreteElementType_NoAFT0005() => Verify(@"
using UnityEngine;
using Aspid.FastTools.Types;
class Concrete { }
class C { [SerializeReference, TypeSelector] private Concrete _value; }");

    [Fact]
    public Task TypeofArgumentWithNoImplementations_ReportsAFT0005() => Verify(@"
using UnityEngine;
using Aspid.FastTools.Types;
interface IBase { }
interface IDerived : IBase { }
class C { [SerializeReference, {|AFT0005:TypeSelector(typeof(IDerived))|}] private IBase _value; }");

    [Fact]
    public Task TypeofArgumentWithConcreteImpl_NoAFT0005() => Verify(@"
using UnityEngine;
using Aspid.FastTools.Types;
interface IBase { }
interface IDerived : IBase { }
class DerivedImpl : IDerived { }
class C { [SerializeReference, TypeSelector(typeof(IDerived))] private IBase _value; }");
}
