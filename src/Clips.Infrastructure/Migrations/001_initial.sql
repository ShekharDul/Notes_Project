CREATE TABLE Notebook (
 Id TEXT PRIMARY KEY, Name TEXT NOT NULL CHECK(length(trim(Name)) BETWEEN 1 AND 100),
 CoverColor TEXT NOT NULL, Icon TEXT, CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL,
 IsArchived INTEGER NOT NULL DEFAULT 0 CHECK(IsArchived IN (0,1)));
CREATE TABLE CaptureSession (
 Id TEXT PRIMARY KEY, NotebookId TEXT NOT NULL REFERENCES Notebook(Id) ON DELETE CASCADE,
 StartedAtUtc TEXT NOT NULL, EndedAtUtc TEXT, Status INTEGER NOT NULL CHECK(Status BETWEEN 0 AND 2), CreatedAtUtc TEXT NOT NULL);
CREATE UNIQUE INDEX IX_SingleOpenSession ON CaptureSession((1)) WHERE Status IN (0,1);
CREATE INDEX IX_Notebook_Updated ON Notebook(UpdatedAtUtc);
CREATE INDEX IX_Session_Notebook_Status ON CaptureSession(NotebookId,Status);
CREATE TABLE CaptureItem (
 Id TEXT PRIMARY KEY, NotebookId TEXT NOT NULL REFERENCES Notebook(Id) ON DELETE CASCADE,
 SessionId TEXT NOT NULL REFERENCES CaptureSession(Id) ON DELETE CASCADE,
 Type INTEGER NOT NULL CHECK(Type IN (0,1)), ContentText TEXT, ImageRelativePath TEXT,
 UserNote TEXT CHECK(length(UserNote)<=10000), SourceAppName TEXT, SourceWindowTitle TEXT,
 SourceUrl TEXT, SourceDocumentName TEXT, SourcePageHint TEXT, CapturedAtUtc TEXT NOT NULL,
 DisplayOrder INTEGER NOT NULL, CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL,
 CHECK((Type=0 AND ContentText IS NOT NULL AND ImageRelativePath IS NULL) OR (Type=1 AND ContentText IS NULL AND ImageRelativePath IS NOT NULL)));
CREATE INDEX IX_Capture_Notebook_Order ON CaptureItem(NotebookId,DisplayOrder);
CREATE INDEX IX_Capture_Session_Time ON CaptureItem(SessionId,CapturedAtUtc);
CREATE TABLE AppSettings (
 Id INTEGER PRIMARY KEY CHECK(Id=1), TextCaptureShortcut TEXT NOT NULL,
 ImageCaptureShortcut TEXT NOT NULL, OpenTrayShortcut TEXT NOT NULL,
 NotificationsEnabled INTEGER NOT NULL, StartMinimizedToTray INTEGER NOT NULL, Theme INTEGER NOT NULL);
INSERT INTO AppSettings VALUES(1,'Ctrl+Alt+S','Ctrl+Alt+A','Ctrl+Alt+N',1,0,0);
PRAGMA user_version=1;
