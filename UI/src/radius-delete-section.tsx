import React from "react";
import { bindValue, trigger, useValue } from "cs2/api";
import { tool } from "cs2/bindings";
import { VanillaComponentResolver } from "./vanilla-resolver";
import styles from "./radius-delete.module.scss";

const toolState$ = bindValue<number>("RadiusDelete", "ToolState");
const radius$ = bindValue<number>("RadiusDelete", "Radius");
const filters$ = bindValue<number>("RadiusDelete", "Filters");

export const RadiusDeleteSection: any = (Component: any) => (props: any) => {
    const toolState = useValue(toolState$);
    const radius = useValue(radius$);
    const filters = useValue(filters$);
    const nativeActiveTool = useValue(tool.activeTool$);

    const isOurToolActive = toolState === 2;
    const isBulldozerPanelOpen = nativeActiveTool.id === "Bulldoze Tool";

    const setRadius = (x: number) => trigger("RadiusDelete", "SetRadius", x);
    const toggleFilter = (f: number) => trigger("RadiusDelete", "SetFilter", f);
    const isFilterOn = (flag: number) => (filters & flag) === flag;

    const result = Component(props);

    if (isBulldozerPanelOpen) {
        const { Section, ToolButton, toolButtonTheme, FOCUS_DISABLED } = VanillaComponentResolver.instance;

        const myUI = (
            <div key="radius-delete-ui" className={styles.container}>
                <Section title="Radius Mode Active">
                    <ToolButton
                        className={toolButtonTheme.button}
                        selected={true}
                        src="Media/Game/Icons/ZoneMarquee.svg"
                        onSelect={() => trigger("RadiusDelete", "ToggleTool")}
                        tooltip="Switch to Single Delete"
                        focusKey={FOCUS_DISABLED}
                    />
                </Section>

                <Section title={`Brush Size: ${Math.round(radius || 20)}m`}>
                    <div className={styles.row}>
                        <button className={styles.adjBtn} onClick={() => setRadius((radius || 20) - 5)}>-</button>
                        <input
                            type="range"
                            className={styles.slider}
                            min={5} max={200} step={5}
                            value={radius || 20}
                            onChange={(e) => setRadius(parseFloat(e.target.value))}
                        />
                        <button className={styles.adjBtn} onClick={() => setRadius((radius || 20) + 5)}>+</button>
                    </div>
                </Section>

                <Section title="Filters">
                    <div className={styles.filterGrid}>
                        <FilterButton flag={1} label="Nets" src="Media/Game/Icons/Roads.svg" current={filters} />
                        <FilterButton flag={2} label="Buildings" src="Media/Game/Icons/ZoneResidential.svg" current={filters} />
                        <FilterButton flag={4} label="Trees" src="Media/Game/Icons/Trees.svg" current={filters} />
                        <FilterButton flag={16} label="Props" src="Media/Game/Icons/Props.svg" current={filters} />
                        <FilterButton flag={32} label="Surfaces" src="Media/Game/Icons/LotTool.svg" current={filters} />
                    </div>
                </Section>
            </div>
        );

        if (isOurToolActive) {
            // HIDE EVERYTHING ELSE: Replace vanilla/other mod children with just our UI
            result.props.children = [myUI];
        } else {
            // VANILLA MODE: Just add our entry button to the bottom
            const entryBtn = (
                <Section title="Radius Delete" key="radius-entry">
                    <ToolButton
                        src="Media/Game/Icons/ZoneMarquee.svg"
                        onSelect={() => trigger("RadiusDelete", "ToggleTool")}
                        focusKey={FOCUS_DISABLED}
                    />
                </Section>
            );
            if (Array.isArray(result.props.children)) result.props.children.push(entryBtn);
            else result.props.children = [result.props.children, entryBtn];
        }
    }

    return result;
};

const FilterButton = ({ flag, label, src, current }: any) => {
    const { ToolButton, toolButtonTheme, FOCUS_DISABLED } = VanillaComponentResolver.instance;
    return (
        <ToolButton
            className={toolButtonTheme.button}
            selected={(current & flag) === flag}
            onSelect={() => trigger("RadiusDelete", "SetFilter", flag)}
            src={src}
            tooltip={label}
            focusKey={FOCUS_DISABLED}
        />
    );
};