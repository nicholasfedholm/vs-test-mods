using System; using System.Reflection;
public class Stub : DispatchProxy {
  public static int HostId = 7;
  public static T Make<T>() => DispatchProxy.Create<T, Stub>();
  protected override object Invoke(MethodInfo m, object[] a) {
    var t = m.ReturnType;
    if (t == typeof(int)) return HostId;
    if (t.IsInterface) return typeof(Stub).GetMethod("Make").MakeGenericMethod(t).Invoke(null, null);
    return t.IsValueType && t != typeof(void) ? Activator.CreateInstance(t) : null;
  }
}
