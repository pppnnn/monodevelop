//
// PackageManagementEventsMonitor.cs
//
// Author:
//       Matt Ward <matt.ward@xamarin.com>
//
// Copyright (c) 2014 Xamarin Inc. (http://xamarin.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MonoDevelop.Core;
using NuGet;
using NuGet.ProjectManagement;

namespace MonoDevelop.PackageManagement
{
	internal class PackageManagementEventsMonitor : IDisposable
	{
		ProgressMonitor progressMonitor;
		IPackageManagementEvents packageManagementEvents;
		IProgressProvider progressProvider;
		FileConflictAction lastFileConflictResolution;
		IFileConflictResolver fileConflictResolver = new FileConflictResolver ();
		string currentProgressOperation;
		List<FileEventArgs> fileChangedEvents = new List<FileEventArgs> ();
		ISolution solutionContainingProjectBuildersToDispose;
		TaskCompletionSource<bool> taskCompletionSource;

		public PackageManagementEventsMonitor (
			ProgressMonitor progressMonitor,
			IPackageManagementEvents packageManagementEvents,
			IProgressProvider progressProvider)
			: this (progressMonitor, packageManagementEvents, progressProvider, null)
		{
		}

		public PackageManagementEventsMonitor (
			ProgressMonitor progressMonitor,
			IPackageManagementEvents packageManagementEvents,
			IProgressProvider progressProvider,
			TaskCompletionSource<bool> taskCompletionSource)
		{
			this.progressMonitor = progressMonitor;
			this.packageManagementEvents = packageManagementEvents;
			this.progressProvider = progressProvider;
			this.taskCompletionSource = taskCompletionSource;

			packageManagementEvents.PackageOperationMessageLogged += PackageOperationMessageLogged;
			packageManagementEvents.ResolveFileConflict += ResolveFileConflict;
			packageManagementEvents.FileChanged += FileChanged;
			packageManagementEvents.ImportRemoved += ImportRemoved;
			progressProvider.ProgressAvailable += ProgressAvailable;
		}

		public void Dispose ()
		{
			progressProvider.ProgressAvailable -= ProgressAvailable;
			packageManagementEvents.ImportRemoved -= ImportRemoved;
			packageManagementEvents.FileChanged -= FileChanged;
			packageManagementEvents.ResolveFileConflict -= ResolveFileConflict;
			packageManagementEvents.PackageOperationMessageLogged -= PackageOperationMessageLogged;

			NotifyFilesChanged ();
			UnloadMSBuildHost ();
		}

		void ResolveFileConflict(object sender, ResolveFileConflictEventArgs e)
		{
			if (UserPreviouslySelectedOverwriteAllOrIgnoreAll ()) {
				e.Resolution = lastFileConflictResolution;
			} else {
				GuiSyncDispatch (() => {
					e.Resolution = fileConflictResolver.ResolveFileConflict (e.Message);
				});
				lastFileConflictResolution = e.Resolution;
			}
		}

		bool UserPreviouslySelectedOverwriteAllOrIgnoreAll()
		{
			return
				(lastFileConflictResolution == FileConflictAction.IgnoreAll) ||
				(lastFileConflictResolution == FileConflictAction.OverwriteAll);
		}

		protected virtual void GuiSyncDispatch (Action action)
		{
			Runtime.RunInMainThread (action).Wait ();
		}

		void PackageOperationMessageLogged (object sender, PackageOperationMessageLoggedEventArgs e)
		{
			if (e.Message.Level == NuGet.MessageLevel.Warning) {
				ReportWarning (e.Message.ToString ());
			} else {
				LogMessage (e.Message.ToString ());
			}
		}

		void ReportWarning (string message)
		{
			progressMonitor.ReportWarning (message);
			LogMessage (message);

			HasWarnings = true;
		}

		void LogMessage (string message)
		{
			progressMonitor.Log.WriteLine (message);
		}

		public bool HasWarnings { get; private set; }

		public void ReportResult (ProgressMonitorStatusMessage progressMessage)
		{
			if (HasWarnings) {
				progressMonitor.ReportWarning (progressMessage.Warning);
			} else {
				progressMonitor.ReportSuccess (progressMessage.Success);
			}

			if (taskCompletionSource != null) {
				taskCompletionSource.TrySetResult (true);
			}
		}

		void ProgressAvailable (object sender, ProgressEventArgs e)
		{
			if (currentProgressOperation == e.Operation)
				return;

			currentProgressOperation = e.Operation;
			progressMonitor.Log.WriteLine (e.Operation);
		}

		void FileChanged (object sender, FileEventArgs e)
		{
			fileChangedEvents.Add (e);
		}

		void NotifyFilesChanged ()
		{
			GuiSyncDispatch (() => {
				FilePath[] files = fileChangedEvents
					.SelectMany (fileChangedEvent => fileChangedEvent.ToArray ())
					.Select (fileInfo => fileInfo.FileName)
					.ToArray ();

				NotifyFilesChanged (files);
			});
		}

		protected virtual void NotifyFilesChanged (FilePath[] files)
		{
			FileService.NotifyFilesChanged (files);
		}

		public void ReportError (ProgressMonitorStatusMessage progressMessage, Exception ex)
		{
			LoggingService.LogError (progressMessage.Error, ex);
			progressMonitor.Log.WriteLine (GetErrorMessageForPackageConsole (ex));
			progressMonitor.ReportError (progressMessage.Error, null);
			ShowPackageConsole (progressMonitor);
			packageManagementEvents.OnPackageOperationError (ex);

			if (taskCompletionSource != null) {
				taskCompletionSource.TrySetException (ExceptionUtility.Unwrap (ex));
			}
		}

		static string GetErrorMessageForPackageConsole (Exception ex)
		{
			var aggregateEx = ex as AggregateException;
			if (aggregateEx != null) {
				var message = new AggregateExceptionErrorMessage (aggregateEx);
				return message.ToString ();
			}
			return ex.Message;
		}

		protected virtual void ShowPackageConsole (ProgressMonitor progressMonitor)
		{
			progressMonitor.ShowPackageConsole ();
		}

		void ImportRemoved (object sender, DotNetProjectImportEventArgs e)
		{
			solutionContainingProjectBuildersToDispose = e.Project.ParentSolution;
		}

		void UnloadMSBuildHost ()
		{
			if (solutionContainingProjectBuildersToDispose == null)
				return;

			GuiSyncDispatch (() => {
				foreach (IDotNetProject project in solutionContainingProjectBuildersToDispose.GetAllProjects ()) {
					project.DisposeProjectBuilder ();
				}
			});
		}
	}
}

