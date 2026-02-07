using Colossal.UI.Binding;
using Game.Tools;
using Game.UI;
using Game.UI.InGame;
using Unity.Entities;
using UnityEngine; // REQUIRED for Input and KeyCode

using UnityEngine.InputSystem; // REQUIRED: Use the new Input System namespace

namespace RadiusDelete
{
    public partial class RadiusDeleteUISystem : UISystemBase
    {
        private const string ModId = "RadiusDelete";
        private ToolSystem m_ToolSystem;
        private RadiusDeleteTool m_RadiusDeleteTool;
        private DefaultToolSystem m_DefaultToolSystem;
        private BulldozeToolSystem m_BulldozeToolSystem;

        private ValueBinding<float> m_RadiusBinding;
        private ValueBinding<int> m_FiltersBinding;
        private ValueBinding<int> m_ActiveToolState; 

        protected override void OnCreate()
        {
            base.OnCreate();
            
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_DefaultToolSystem = World.GetOrCreateSystemManaged<DefaultToolSystem>();
            m_RadiusDeleteTool = World.GetOrCreateSystemManaged<RadiusDeleteTool>();
            m_BulldozeToolSystem = World.GetOrCreateSystemManaged<BulldozeToolSystem>();

            AddBinding(m_RadiusBinding = new ValueBinding<float>(ModId, "Radius", 20f));
            AddBinding(m_FiltersBinding = new ValueBinding<int>(ModId, "Filters", (int)DeleteFilters.All));
            AddBinding(m_ActiveToolState = new ValueBinding<int>(ModId, "ToolState", 0));

            AddBinding(new TriggerBinding(ModId, "ToggleTool", ToggleTool));
            AddBinding(new TriggerBinding<float>(ModId, "SetRadius", SetRadius));
            AddBinding(new TriggerBinding<int>(ModId, "SetFilter", SetFilter));

            m_ToolSystem.EventToolChanged += OnToolChanged;
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();

            // FIX: Replaced 'Input.GetKey' with 'Keyboard.current' (New Input System)
            // Debug Hotkey: Ctrl + K
            if (Keyboard.current != null && 
                Keyboard.current.kKey.wasPressedThisFrame && 
                (Keyboard.current.leftCtrlKey.isPressed || Keyboard.current.rightCtrlKey.isPressed))
            {
                ToggleTool();
                
                // Ensure RadiusDeleteMod.Log is 'public static' in your Mod class
                RadiusDeleteMod.Log.Info("RadiusDelete forced toggle via hotkey.");
            }

            UpdateToolState(m_ToolSystem.activeTool);
        }

        private void ToggleTool()
        {
            if (m_ToolSystem.activeTool == m_RadiusDeleteTool)
            {
                m_ToolSystem.activeTool = m_BulldozeToolSystem;
            }
            else
            {
                m_ToolSystem.activeTool = m_RadiusDeleteTool;
            }
        }

        private void SetRadius(float radius)
        {
            m_RadiusDeleteTool.Radius = Mathf.Clamp(radius, 5f, 500f);
            m_RadiusBinding.Update(m_RadiusDeleteTool.Radius);
        }

        private void SetFilter(int filterFlag)
        {
            var filter = (DeleteFilters)filterFlag;
            if ((m_RadiusDeleteTool.ActiveFilters & filter) == filter)
                m_RadiusDeleteTool.ActiveFilters &= ~filter;
            else
                m_RadiusDeleteTool.ActiveFilters |= filter;

            m_FiltersBinding.Update((int)m_RadiusDeleteTool.ActiveFilters);
        }

        private void OnToolChanged(ToolBaseSystem tool)
        {
            int state = 0;
            if (tool != null)
            {
                // Reference check ensures we distinguish "Our Imposter Tool" from "Vanilla Tool"
                // even though they share the same ID string.
                if (tool == m_RadiusDeleteTool) 
                {
                    state = 2; 
                }
                else if (tool == m_BulldozeToolSystem || tool.toolID == "Bulldoze Tool")
                {
                    state = 1;
                }
            }
            m_ActiveToolState.Update(state);
        }

        // Inside RadiusDeleteUISystem.cs

        private void UpdateToolState(ToolBaseSystem tool)
        {
            int state = 0;
            if (tool != null)
            {
                // Reference check is now mandatory because IDs are identical
                if (tool == m_RadiusDeleteTool)
                {
                    state = 2; // Our custom Radius Mode
                }
                else if (tool.toolID == "Bulldoze Tool")
                {
                    state = 1; // Vanilla or other Bulldoze mods
                }
            }
            m_ActiveToolState.Update(state);
        }
        
        protected override void OnDestroy()
        {
            base.OnDestroy();
            m_ToolSystem.EventToolChanged -= OnToolChanged;
        }
    }
}