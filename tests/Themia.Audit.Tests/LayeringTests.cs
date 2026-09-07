using System;
using System.Linq;
using Xunit;

namespace Themia.Audit.Tests;

public class LayeringTests
{
    [Fact]
    public void Themia_Audit_references_no_framework_package()
    {
        var referenced = typeof(AuditEntry).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("Themia.Framework.", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(referenced);
    }
}
