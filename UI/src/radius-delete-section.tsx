import React from "react";
import { bindValue, trigger, useValue } from "cs2/api";
import { tool } from "cs2/bindings";
import { VanillaComponentResolver } from "./vanilla-resolver";
import styles from "./radius-delete.module.scss";

// Bindings
const toolState$ = bindValue<number>("RadiusDelete", "ToolState");
const radius$ = bindValue<number>("RadiusDelete", "Radius");
const filters$ = bindValue<number>("RadiusDelete", "Filters");

// ICON FIX: Embedded Base64 SVG. 
// This creates a simple white circle icon. No external files needed.
const iconCircle = "data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCA1MCA1MCI+CiAgPGNpcmNsZSBjeD0iMjUiIGN5PSIyNSIgcj0iMjAiIGZpbGw9Im5vbmUiIHN0cm9rZT0id2hpdGUiIHN0cm9rZS13aWR0aD0iMyIgLz4KPC9zdmc+";

export const RadiusDeleteSection: any = (Component: any) => (props: any) => {
    const toolState = useValue(toolState$);
    const radius = useValue(radius$);
    const filters = useValue(filters$);
    const nativeActiveTool = useValue(tool.activeTool$);

    const isBulldozerActive = nativeActiveTool.id === "Bulldoze Tool" || toolState > 0;

    const toggleTool = () => trigger("RadiusDelete", "ToggleTool");
    const setRadius = (x: number) => trigger("RadiusDelete", "SetRadius", x);

    // Call original component
    const result = Component(props);

    if (isBulldozerActive) {
        if (!result.props.children) result.props.children = [];

        const Section = VanillaComponentResolver.instance.Section;
        const ToolButton = VanillaComponentResolver.instance.ToolButton;
        const theme = VanillaComponentResolver.instance.toolButtonTheme;
        const FOCUS_DISABLED = VanillaComponentResolver.instance.FOCUS_DISABLED;

        // PART 1: The Toggle Button
        // We keep 'Section' here because it only has ONE child (safe).
        const toggleSection = (
            <Section title="Radius Delete" key="radius-toggle-section">
                <ToolButton
                    className={theme.button}
                    selected={toolState === 2}
                    src={iconCircle} // Using Base64 icon
                    onSelect={toggleTool}
                    tooltip="Toggle Radius Delete Mode"
                    focusKey={FOCUS_DISABLED}
                />
            </Section>
        );

        // PART 2: The Settings Panel
        // CRITICAL FIX: We do NOT use 'Section' here. We use a plain div.
        // This avoids the "Focus node can only host a single child" crash completely.
        const settingsSection = toolState === 2 ? (
            <div key="radius-settings-panel" className={styles.panelContainer}>
                <div className={styles.panelHeader}>Radius Settings</div>

                {/* Slider */}
                <div className={styles.row}>
                    <div className={styles.label}>Size: {Math.round(radius || 20)}m</div>
                    <input
                        type="range"
                        className={styles.slider}
                        min={5} max={200} step={5}
                        value={radius || 20}
                        onChange={(e) => setRadius(parseFloat(e.target.value))}
                    />
                </div>
            </div>
        ) : null;

        // Injection
        if (Array.isArray(result.props.children)) {
            result.props.children.push(toggleSection);
            if (settingsSection) result.props.children.push(settingsSection);
        } else {
            result.props.children = [result.props.children, toggleSection];
            if (settingsSection) result.props.children.push(settingsSection);
        }
    }

    return result;
};