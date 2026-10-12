using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using LiveSplit.Model;

namespace LiveSplit.Coop {
    // The native graph has a separate gold color and does not use LiveSplit's
    // rainbow option. Follow the same native color as the split/timer display.
    public sealed class RainbowGraph {
        readonly Dictionary<object,Color> original=new Dictionary<object,Color>();
        const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        public bool Update(LiveSplitState state) {
            if(state.Layout==null)return false;
            bool changed=false;
            var active=new HashSet<object>();
            foreach(var component in state.Layout.Components) {
                if(component.GetType().FullName!="LiveSplit.UI.Components.GraphCompositeComponent")continue;
                var settingsProperty=component.GetType().GetProperty("Settings",Flags);
                if(settingsProperty==null)continue;
                var settings=settingsProperty.GetValue(component,null);
                var colorProperty=settings.GetType().GetProperty("GraphGoldColor");
                if(colorProperty==null || !colorProperty.CanWrite)continue;
                active.Add(settings);
                Color previous;
                if(state.LayoutSettings.UseRainbowColor) {
                    if(!original.ContainsKey(settings))original[settings]=(Color)colorProperty.GetValue(settings,null);
                    colorProperty.SetValue(settings,LiveSplitStateHelper.GetBestSegmentColor(state),null);
                    changed=true;
                } else if(original.TryGetValue(settings,out previous)) {
                    colorProperty.SetValue(settings,previous,null);original.Remove(settings);changed=true;
                }
            }
            var removed=new List<object>();
            foreach(var key in original.Keys)if(!active.Contains(key))removed.Add(key);
            foreach(var key in removed)original.Remove(key);
            return changed;
        }
    }
}
