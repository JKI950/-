/* ── Tab navigation ───────────────────────────────────────── */
const tabBtns   = document.querySelectorAll('.tab-btn');
const tabPanels = document.querySelectorAll('.tab-panel');

tabBtns.forEach(btn => {
  btn.addEventListener('click', () => {
    const target = btn.dataset.tab;

    tabBtns.forEach(b => b.classList.toggle('active', b === btn));
    tabPanels.forEach(p => p.classList.toggle('active', p.id === `panel-${target}`));
  });
});

/* ── Edit-profile form ────────────────────────────────────── */
const editForm     = document.getElementById('edit-form');
const nameDisplay  = document.getElementById('display-name');
const handleDisplay = document.getElementById('display-handle');
const bioDisplay   = document.getElementById('display-bio');

editForm && editForm.addEventListener('submit', e => {
  e.preventDefault();
  const data = Object.fromEntries(new FormData(editForm));

  if (data.name)     nameDisplay.textContent   = data.name;
  if (data.handle)   handleDisplay.textContent = '@' + data.handle.replace(/^@/, '');
  if (data.bio)      bioDisplay.textContent    = data.bio;

  showToast('Profile updated successfully ✓');

  /* Switch back to About tab */
  tabBtns.forEach(b => b.classList.toggle('active', b.dataset.tab === 'about'));
  tabPanels.forEach(p => p.classList.toggle('active', p.id === 'panel-about'));
});

/* ── Cancel edit ──────────────────────────────────────────── */
const cancelBtn = document.getElementById('cancel-edit');
cancelBtn && cancelBtn.addEventListener('click', () => {
  tabBtns.forEach(b => b.classList.toggle('active', b.dataset.tab === 'about'));
  tabPanels.forEach(p => p.classList.toggle('active', p.id === 'panel-about'));
});

/* ── Avatar file picker (cosmetic) ───────────────────────── */
const avatarEditBtn = document.querySelector('.avatar-edit-btn');
const avatarImg     = document.querySelector('.avatar-img');

avatarEditBtn && avatarEditBtn.addEventListener('click', () => {
  const input = document.createElement('input');
  input.type  = 'file';
  input.accept = 'image/*';
  input.onchange = () => {
    const file = input.files[0];
    if (!file || !file.type.startsWith('image/')) return;
    const reader = new FileReader();
    reader.onload = (evt) => {
      const result = evt.target.result;
      if (typeof result === 'string' && result.startsWith('data:image/')) {
        avatarImg.src = result;
        avatarImg.style.display = 'block';
        const initials = document.querySelector('.avatar-initials');
        if (initials) initials.style.display = 'none';
        showToast('Avatar updated ✓');
      }
    };
    reader.readAsDataURL(file);
  };
  input.click();
});

/* ── Follow button toggle ─────────────────────────────────── */
const followBtn = document.getElementById('follow-btn');
let following   = false;

followBtn && followBtn.addEventListener('click', () => {
  following = !following;
  followBtn.textContent = following ? '✓ Following' : '+ Follow';
  followBtn.classList.toggle('btn-outline', following);
  followBtn.classList.toggle('btn-primary', !following);

  const followerEl = document.getElementById('stat-followers');
  if (followerEl) {
    let count = parseInt(followerEl.textContent.replace(/,/g, ''), 10);
    count += following ? 1 : -1;
    followerEl.textContent = count.toLocaleString();
  }
});

/* ── Toast helper ─────────────────────────────────────────── */
function showToast(msg) {
  const toast = document.getElementById('toast');
  if (!toast) return;
  toast.textContent = msg;
  toast.classList.add('show');
  setTimeout(() => toast.classList.remove('show'), 2800);
}
