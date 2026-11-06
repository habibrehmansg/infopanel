{ The installer's check for an installed .NET runtime the sensor helper can use, kept apart from InfoPanel.iss so that a
  test script can include it alone. Included into InfoPanel.iss's [Code]. }

{ Whether Name is a stable runtime's folder name, three whole numbers ("10.0.14"), and its first number. A prerelease
  ("11.0.0-preview.1", "9.0.0-rc.2.24473.5") isn't: the helper rolls forward only to stable runtimes. }
function IsStableVersion(const Name: String; var Major: Integer): Boolean;
var
  Rest, Part: String;
  Dot, Parts, I: Integer;
begin
  Result := False;
  Major := -1;
  Rest := Name;
  Parts := 0;
  repeat
    Dot := Pos('.', Rest);
    if Dot > 0 then
    begin
      Part := Copy(Rest, 1, Dot - 1);
      Rest := Copy(Rest, Dot + 1, Length(Rest));
    end
    else
    begin
      Part := Rest;
      Rest := '';
    end;
    if (Part = '') or (Length(Part) > 9) then
      Exit;
    for I := 1 to Length(Part) do
      if (Part[I] < '0') or (Part[I] > '9') then
        Exit;
    if Parts = 0 then
      Major := StrToInt(Part);
    Parts := Parts + 1;
  until Dot = 0;
  Result := Parts = 3;
end;

{ Whether <Root>\shared\Microsoft.NETCore.App holds a stable runtime 8.0 or later: a folder named as one, with its
  coreclr.dll (a folder left half-removed doesn't count). Root is an x64 .NET's install location. }
function HasNetCore8(const Root: String): Boolean;
var
  FindRec: TFindRec;
  Shared: String;
  Major: Integer;
begin
  Result := False;
  Shared := AddBackslash(Root) + 'shared\Microsoft.NETCore.App\';
  if FindFirst(Shared + '*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0) and IsStableVersion(FindRec.Name, Major) and (Major >= 8)
          and FileExists(Shared + FindRec.Name + '\coreclr.dll') then
          Result := True;
      until Result or not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;
