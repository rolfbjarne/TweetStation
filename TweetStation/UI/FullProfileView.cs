// Copyright 2010 Miguel de Icaza
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
using System.Drawing;
using System.IO;
using System.Linq;
using MonoTouch.Foundation;
using MonoTouch.Dialog;
using MonoTouch.UIKit;
using System.Web;
using System.Json;

namespace TweetStation
{
	public class FullProfileView : DialogViewController 
	{
		UIActivityIndicatorView spinner;
		const int PadX = 4;
		StyledStringElement followButton, blockUnblockButton;
		User user;
		bool following, blocking;
		
		public FullProfileView (long id) : base (UITableViewStyle.Grouped, null, true)
		{
			user = User.FromId (id);
			if (user == null)
				User.FetchUser (id, u => ProcessUserReturn (u));
			else 
				CreateUI ();
		}

		public FullProfileView (string name) : base (UITableViewStyle.Grouped, null, true)
		{
			user = User.FromName (name);
			if (user == null)
				User.FetchUser (name, u => ProcessUserReturn (u));
			else
				CreateUI ();
		}
		
		public override void ViewWillAppear (bool animated)
		{
			base.ViewWillAppear (animated);
			
			// If the user is null, add a spinner to show that the data is loading
			if (user == null){
				spinner = new UIActivityIndicatorView (UIActivityIndicatorViewStyle.White){
					Frame = new RectangleF (20, 20, 30, 30)
				};
				spinner.StartAnimating ();
				View.AddSubview (spinner);
			}
		}
		
		void ProcessUserReturn (User user)
		{
			if (user == null){
				Root = Util.MakeError (Locale.GetText ("Could not get information on user"));
				return;
			}
			this.user = user;
			CreateUI ();
		}
		
		void CreateUI ()
		{
			System.Threading.Thread.Sleep (2000);
			if (spinner != null){
				spinner.RemoveFromSuperview ();
				spinner = null;
			}
			
			var profileRect = new RectangleF (PadX, 0, View.Bounds.Width-PadX, 100);
			var shortProfileView = new ShortProfileView (profileRect, user.Id, false);
			shortProfileView.PictureTapped += delegate { PictureViewer.Load (this, user.Id); };
			shortProfileView.UrlTapped += delegate { WebViewController.OpenUrl (this, user.Url); };

			var main = new Section (shortProfileView);
			if (!String.IsNullOrEmpty (user.Description)){
				main.Add (new StyledMultilineElement (user.Description) {
					Lines = 0,
					LineBreakMode = UILineBreakMode.WordWrap,
					Font = UIFont.SystemFontOfSize (14), 
				});
			};
			
			var tweetsUrl = String.Format ("http://api.twitter.com/1/statuses/user_timeline.json?skip_user=true&id={0}", user.Id);
			var favoritesUrl = String.Format ("http://api.twitter.com/1/favorites.json?id={0}", user.Id);
			var followersUrl = String.Format ("http://api.twitter.com/1/statuses/followers.json?id={0}", user.Id);
			var friendsUrl = String.Format ("http://api.twitter.com/1/statuses/friends.json?id={0}", user.Id);
			
#if false
			followButton = new StyledStringElement (FollowText, ToggleFollow){
				Alignment = UITextAlignment.Center,
				TextColor = UIColor.FromRGB (0x32, 0x4f, 0x85)
			};
#endif	
			var sfollow = new Section () {
				new ActivityElement ()
			};
			
			Root = new RootElement (user.Screenname){
				main,
				new Section () {
					(Element) TimelineRootElement.MakeTimeline (user.Screenname, Locale.Format ("{0:#,#} tweets", user.StatusesCount), tweetsUrl, user),
					(Element) TimelineRootElement.MakeFavorites (user.Screenname, Locale.Format ("{0:#,#} favorites", user.FavCount), favoritesUrl, null),
					(Element) new UserRootElement (user, Locale.Format ("{0:#,#} friends", user.FriendsCount), friendsUrl),
					(Element) new UserRootElement (user, Locale.Format ("{0:#,#} followers", user.FollowersCount), followersUrl),
				},
				sfollow,
			};
			var created = user.CreatedAt;
			if (created.HasValue){
				Root.Add (new Section (null, Locale.Format ("Joined on {0}", created.Value.ToLongDateString ())));
			}
			
			string url = String.Format ("http://api.twitter.com/1/friendships/show.json?target_id={0}&source_screen_name={1}",
			                            user.Id, 
			                            OAuth.PercentEncode (TwitterAccount.CurrentAccount.Username));
			TwitterAccount.CurrentAccount.Download (url, res => {
				TableView.BeginUpdates ();
				Root.Remove (sfollow);
				if (res != null)
					ParseFollow (res);

				TableView.EndUpdates ();
			});
		}

		static string GetFollowText (bool following)
		{
			if (following)
				return Locale.GetText ("Unfollow this user");
			else
				return Locale.GetText ("Follow this user");
		}
		
		static string GetBlockText (bool blocking)
		{
			if (blocking)
				return Locale.GetText ("Unblock this user");
			else
				return Locale.GetText ("Block this user");
		}
		
		// 
		// Parses the return from twitter from the friendship show result
		// we extract from here the follow status and the blocking status
		// and insert the sections directly into our root
		//
		void  ParseFollow (Stream res)
		{
			try {
				var root = JsonValue.Load (res);
				
				//
				// Follow/unfollow
				//
				var target = root ["relationship"]["target"];
				following = target ["followed_by"];

				followButton = new StyledStringElement (GetFollowText (following), ToggleFollow){
					Alignment = UITextAlignment.Center,
					TextColor = UIColor.FromRGB (0x32, 0x4f, 0x85),
					Font = UIFont.BoldSystemFontOfSize (16),
				};

				var following_me = (bool) target ["following"];
				var caption = following_me ? Locale.Format ("{0} is following you", user.Screenname) : Locale.Format ("{0} is not following you", user.Screenname);
				Root.Insert (2, new Section (null, caption) { followButton });
				
				// 
				// Block/unblock
				//
				var source = root ["relationship"]["source"];
				var blocking = (bool) source ["blocking"];
				blockUnblockButton = new StyledStringElement (GetBlockText (blocking), ToggleBlock){
					Font = UIFont.BoldSystemFontOfSize (16),
					Alignment = UITextAlignment.Center,
					TextColor = UIColor.FromRGB (0x32, 0x4f, 0x85),
				};
				Root.Insert (3, new Section () { blockUnblockButton });
			} catch (Exception e) {
				Console.WriteLine (e);
			}
		}
		
		void ToggleFollow ()
		{
			var url = String.Format ("http://api.twitter.com/1/friendships/{0}/{1}.json", following ? "destroy": "create", user.Id);
			following = !following;
			TwitterAccount.CurrentAccount.Post (url, "");
			followButton.Caption = GetFollowText (following); 
			Root.Reload (followButton, UITableViewRowAnimation.Fade);
		}
		
		void ToggleBlock ()
		{
			string caption = blocking
				? Locale.Format ("Are you sure you want to unblock {0}", user.Screenname) 
				: Locale.Format ("Are you sure you want to block {0}", user.Screenname);
			
			var sheet = Util.GetSheet (caption);
			sheet.AddButton (blocking ? Locale.GetText ("Unblock") : Locale.GetText ("Block"));
			sheet.AddButton (Locale.GetText ("Cancel"));
			sheet.CancelButtonIndex = 1;
			sheet.Clicked += delegate(object sender, UIButtonEventArgs e) {
				if (e.ButtonIndex != 0)
					return;
				
				var url = String.Format ("http://api.twitter.com/1/blocks/{0}.json?user_id={1}", blocking ? "destroy" : "create", user.Id);
				blocking = !blocking;
				TwitterAccount.CurrentAccount.Post (url, "");
				blockUnblockButton.Caption = GetBlockText (blocking);;
				Root.Reload (blockUnblockButton, UITableViewRowAnimation.Fade);
			};
			
			// You would think "View" is the right view to pass here, but
			// the "Cancel" event wont get events because the View is covered
			// by the tab bar, so it wonget get events.   So we need to find
			// the full root.
			sheet.ShowInView (AppDelegate.MainAppDelegate.MainView);
		}		
	}
		
	public class MyProfileElement : RootElement {
		public MyProfileElement (string caption) : base (caption) {}
		
		public override void Selected (DialogViewController dvc, UITableView tableView, NSIndexPath path)
		{
			var full = new FullProfileView (TwitterAccount.CurrentAccount.Username);
			dvc.ActivateController (full);
		}
	}
}

