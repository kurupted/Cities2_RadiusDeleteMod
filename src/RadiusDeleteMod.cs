using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;

namespace RadiusDelete
{
    public class RadiusDeleteMod : IMod
    {
        
        public static ILog Log = LogManager.GetLogger("RadiusDelete");
        
        public void OnLoad(UpdateSystem updateSystem)
        {
            // Register Systems
            updateSystem.UpdateAt<RadiusDeleteTool>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<RadiusDeleteUISystem>(SystemUpdatePhase.UIUpdate);
        }

        public void OnDispose()
        {
            // Cleanup if necessary
        }
    }
}