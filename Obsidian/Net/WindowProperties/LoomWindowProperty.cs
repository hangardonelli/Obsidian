﻿namespace Obsidian.Net.WindowProperties;

public class LoomWindowProperty : IWindowProperty
{
    public short Property { get; }

    public short Value { get; }

    public LoomWindowProperty(short selectedPatternIndex)
    {
        this.Property = 0;
        this.Value = selectedPatternIndex;
    }
}
