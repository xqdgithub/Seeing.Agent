using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Components;
namespace Seeing.Agent.WebUI.State
{
  public static class SlotRegistry
  {
     private static readonly Dictionary<string, RenderFragment> _slots = new Dictionary<string, RenderFragment>();
     public static void Register(string name, RenderFragment fragment)
     {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Slot name cannot be empty.", nameof(name));
        _slots[name] = fragment;
     }
     internal static RenderFragment? GetSlot(string name)
     {
        if (_slots.TryGetValue(name, out var fragment)) return fragment;
        return null;
     }
  }
}
