import { ModRegistrar } from "cs2/modding";
import { RadiusDeleteSection } from "./radius-delete-section";
import { VanillaComponentResolver } from "./vanilla-resolver"; // Import the class

const register: ModRegistrar = (moduleRegistry) => {
    // 1. INITIALIZE RESOLVER (Critical Step!)
    VanillaComponentResolver.setRegistry(moduleRegistry);

    // 2. Inject UI
    moduleRegistry.extend(
        "game-ui/game/components/tool-options/mouse-tool-options/mouse-tool-options.tsx",
        'MouseToolOptions',
        RadiusDeleteSection
    );

    console.log("RadiusDelete UI Registered");
}

export default register;