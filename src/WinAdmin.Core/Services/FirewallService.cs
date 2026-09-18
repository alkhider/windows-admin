using System.Runtime.InteropServices;
using WinAdmin.Core.Models;

namespace WinAdmin.Core.Services;

public sealed class FirewallService
{
    public string GetProfileSummary()
    {
        try
        {
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
            int types = policy.CurrentProfileTypes;
            Marshal.ReleaseComObject(policy);
            var names = new List<string>();
            if ((types & 1) != 0) names.Add("Domain");
            if ((types & 2) != 0) names.Add("Private");
            if ((types & 4) != 0) names.Add("Public");
            return names.Count > 0 ? string.Join(", ", names) : "Unknown";
        }
        catch
        {
            return "Unavailable";
        }
    }

    public IReadOnlyList<FirewallRuleItem> ListRules(int max = 100)
    {
        var list = new List<FirewallRuleItem>();
        try
        {
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)
                ?? throw new InvalidOperationException("Firewall COM unavailable.");
            dynamic rules = policy.Rules;
            int count = rules.Count;
            for (var i = 1; i <= count && list.Count < max; i++)
            {
                try
                {
                    dynamic rule = rules.Item(i);
                    list.Add(new FirewallRuleItem
                    {
                        Name = rule.Name ?? "",
                        Direction = rule.Direction == 1 ? "Inbound" : "Outbound",
                        Enabled = rule.Enabled,
                        Action = rule.Action == 1 ? "Allow" : "Block"
                    });
                }
                catch
                {
                    // skip
                }
            }

            Marshal.ReleaseComObject(rules);
            Marshal.ReleaseComObject(policy);
        }
        catch
        {
            // ignore
        }

        return list;
    }

    public bool IsEnabled()
    {
        try
        {
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
            int types = policy.CurrentProfileTypes;
            var on = true;
            if ((types & 1) != 0) on &= (bool)policy.FirewallEnabled[1];
            if ((types & 2) != 0) on &= (bool)policy.FirewallEnabled[2];
            if ((types & 4) != 0) on &= (bool)policy.FirewallEnabled[4];
            Marshal.ReleaseComObject(policy);
            return on;
        }
        catch
        {
            return true;
        }
    }

    public OperationResult EnableFirewall()
    {
        try
        {
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
            policy.FirewallEnabled[1] = true;
            policy.FirewallEnabled[2] = true;
            policy.FirewallEnabled[4] = true;
            Marshal.ReleaseComObject(policy);
            return OperationResult.Success("Windows Firewall is on for Domain, Private, and Public.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
    }

    public OperationResult SetRuleEnabled(string name, bool enabled)
    {
        try
        {
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
            dynamic rules = policy.Rules;
            int count = rules.Count;
            for (var i = 1; i <= count; i++)
            {
                dynamic rule = rules.Item(i);
                if (string.Equals((string)rule.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    rule.Enabled = enabled;
                    Marshal.ReleaseComObject(rules);
                    Marshal.ReleaseComObject(policy);
                    return OperationResult.Success($"Rule '{name}' {(enabled ? "enabled" : "disabled")}.");
                }
            }

            Marshal.ReleaseComObject(rules);
            Marshal.ReleaseComObject(policy);
            return OperationResult.Fail("Rule not found.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
    }
}
