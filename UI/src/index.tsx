import { ModRegistrar } from "cs2/modding";
import { RadiusDeleteSection } from "./radius-delete-section";
import { VanillaComponentResolver } from "./vanilla-resolver";

const register: ModRegistrar = (moduleRegistry) => {
    VanillaComponentResolver.setRegistry(moduleRegistry);
    moduleRegistry.extend(
        "game-ui/game/components/tool-options/mouse-tool-options/mouse-tool-options.tsx",
        'MouseToolOptions',
        RadiusDeleteSection
    );
}

export default register;