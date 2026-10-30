# -*- coding: utf-8 -*-
"""Loads the Team Hub engine (bin/TeamHub.Revit.dll) once per Revit session.

The DLL is read into memory instead of loaded from disk, so the file is never
locked: updating the extension works while Revit is open (it takes effect the
next time Revit starts). Every button reuses the same loaded copy, so they all
share one session (login, live sync, panel)."""
import os
import clr
from System import AppDomain
from System.IO import File
from System.Reflection import Assembly

KEY = 'TeamHub.Revit.Assembly'
DLL = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'bin', 'TeamHub.Revit.dll')


def engine():
    asm = AppDomain.CurrentDomain.GetData(KEY)
    if asm is None:
        asm = Assembly.Load(File.ReadAllBytes(DLL))
        AppDomain.CurrentDomain.SetData(KEY, asm)
    clr.AddReference(asm)
    import TeamHub.Revit
    return TeamHub.Revit
